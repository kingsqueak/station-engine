// SPDX-License-Identifier: GPL-2.0-or-later
//
// SimpleHPSDR: record and play back audio.
//
// Records what a receiver hears (its demodulated audio after the RX filter,
// before mute and the speaker mix) or the microphone, as 48 kHz mono 16-bit
// WAV files in one folder (Music/SimpleHPSDR). Plays a file on the air: it
// keys MOX as MoxSource.Wav, through every TX admission and safety check, and
// feeds the clip into the TX audio path in place of the live mic, so the TX
// filter shapes it. A receiver recording skips mic gain and the speech
// processing (it was processed once already); a microphone recording goes
// through them like live speech. SimpleHPSDR did this with its recorder plugin; the
// plugin host is gone from this fork, and its TX hooks (MicBlockSource.Wav,
// MoxSource.Wav) are what this uses.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Server;

public sealed record RecorderStatus(
    string Folder,
    bool Recording,
    string? RecordingSource,
    string? RecordingFile,
    double RecordingSeconds,
    bool Playing,
    bool PlayingLocal,
    string? PlayingFile,
    double PlayPositionSeconds,
    double PlayLengthSeconds,
    string? LastError);

/// <summary>Source: "rx" (a receiver; Receiver 0 = RX1, 1 = RX2) or "mic".</summary>
public sealed record RecorderRecordRequest(string File, string Source, int Receiver = 0);

/// <summary>GainDb on top of the clip (-20..+20). Processed: through mic gain
/// and the TX processing like live speech (microphone recordings). Local: play
/// it through the engine's audio output (mixed with receive) instead of the air.</summary>
public sealed record RecorderPlayRequest(string File, double GainDb = 0, bool Processed = false, bool Local = false);

public sealed class AudioRecorderService : IDisposable
{
    public const int RateHz = 48000;
    private const int BlockSamples = 960;              // 20 ms, the mic block size
    private const double MaxRecordSeconds = 60 * 60;   // stops by itself after an hour
    private const double MaxPlaySeconds = 10 * 60;     // longest clip it will transmit
    private const float ReceiverPeak = 0.7f;           // receiver clips are peak-normalised to this
    private const float LocalPeak = 0.4f;              // and anything played to the speakers to this

    private readonly DspPipelineService _pipeline;
    private readonly TxService _tx;
    private readonly RadioService _radio;
    private readonly IServiceProvider _services;
    private readonly ILogger<AudioRecorderService> _log;
    private readonly object _sync = new();

    private Recording? _recording;
    private Playback? _playback;
    private string? _lastError;

    public AudioRecorderService(
        DspPipelineService pipeline, TxService tx, RadioService radio,
        IServiceProvider services, ILogger<AudioRecorderService> log)
    {
        _pipeline = pipeline;
        _tx = tx;
        _radio = radio;
        _services = services;
        _log = log;
    }

    public static string Folder
    {
        get
        {
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrEmpty(music))
                music = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");
            return Path.Combine(music, "SimpleHPSDR");
        }
    }

    public RecorderStatus Status()
    {
        lock (_sync)
        {
            var r = _recording;
            var p = _playback;
            return new RecorderStatus(Folder,
                r is not null, r?.Source, r?.File, r?.Seconds ?? 0,
                p is not null, p?.Local ?? false, p?.File, p?.PositionSeconds ?? 0, p?.LengthSeconds ?? 0,
                _lastError);
        }
    }

    /// <summary>Only a plain .wav file name inside the recordings folder.</summary>
    private static string? ResolvePath(string? file, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(file) || file != Path.GetFileName(file) || file.Contains("..")
            || !file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
            || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "Give a plain .wav file name.";
            return null;
        }
        return Path.Combine(Folder, file);
    }

    // ---- recording ----------------------------------------------------

    public bool StartRecording(RecorderRecordRequest req, out string? error)
    {
        var path = ResolvePath(req.File, out error);
        if (path is null) return false;
        bool mic = string.Equals(req.Source, "mic", StringComparison.OrdinalIgnoreCase);
        if (!mic && !string.Equals(req.Source, "rx", StringComparison.OrdinalIgnoreCase))
        {
            error = "Source is rx or mic.";
            return false;
        }
        TxAudioIngest? ingest = null;
        if (mic)
        {
            ingest = _services.GetService(typeof(TxAudioIngest)) as TxAudioIngest;
            if (ingest is null) { error = "The microphone path isn't running."; return false; }
        }
        lock (_sync)
        {
            if (_recording is not null) { error = "Already recording."; return false; }
            if (File.Exists(path)) { error = "That file already exists."; return false; }
            try
            {
                Directory.CreateDirectory(Folder);
                var rec = new Recording(path, mic ? "mic" : "rx", Math.Clamp(req.Receiver, 0, 7), _log);
                if (mic)
                {
                    rec.Ingest = ingest;
                    ingest!.MicSamplesObserved += rec.OnMic;
                }
                else
                {
                    _pipeline.ReceiverAudioAvailable += rec.OnReceiver;
                }
                rec.Finished += () => StopRecording();
                _recording = rec;
                _lastError = null;
                _log.LogInformation("recorder.record.start file={File} source={Source} rx={Rx}", req.File, rec.Source, rec.Receiver);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"Can't write the file: {ex.Message}";
                return false;
            }
        }
    }

    public void StopRecording()
    {
        Recording? rec;
        lock (_sync)
        {
            rec = _recording;
            _recording = null;
        }
        if (rec is null) return;
        if (rec.Ingest is { } ingest) ingest.MicSamplesObserved -= rec.OnMic;
        else _pipeline.ReceiverAudioAvailable -= rec.OnReceiver;
        rec.Close();
        _log.LogInformation("recorder.record.stop file={File} seconds={Seconds:0.0}", rec.File, rec.Seconds);
    }

    private sealed class Recording
    {
        private readonly FileStream _stream;
        private readonly Channel<byte[]> _queue = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true });
        private readonly Task _writer;
        private readonly ILogger _log;
        private long _samples;
        private int _finishedFired;

        public string File { get; }
        public string Source { get; }
        public int Receiver { get; }
        public TxAudioIngest? Ingest { get; set; }
        public event Action? Finished;
        public double Seconds => Interlocked.Read(ref _samples) / (double)RateHz;

        public Recording(string path, string source, int receiver, ILogger log)
        {
            File = Path.GetFileName(path);
            Source = source;
            Receiver = receiver;
            _log = log;
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            _stream.Write(new byte[44]);    // header, filled in on Close
            _writer = Task.Run(WriteLoopAsync);
        }

        public void OnReceiver(int receiver, int rateHz, ReadOnlyMemory<float> samples)
        {
            if (receiver != Receiver) return;
            if (rateHz != RateHz) return;   // the engine's audio clock is 48 kHz
            Append(samples.Span);
        }

        public void OnMic(ReadOnlySpan<float> samples) => Append(samples);

        // On the audio thread: convert and queue, never touch the disk here.
        private void Append(ReadOnlySpan<float> samples)
        {
            if (samples.IsEmpty || Volatile.Read(ref _finishedFired) != 0) return;
            var bytes = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                float v = float.IsFinite(samples[i]) ? Math.Clamp(samples[i], -1f, 1f) : 0f;
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)Math.Round(v * 32767f));
            }
            long total = Interlocked.Add(ref _samples, samples.Length);
            _queue.Writer.TryWrite(bytes);
            if (total >= MaxRecordSeconds * RateHz && Interlocked.Exchange(ref _finishedFired, 1) == 0)
                ThreadPool.QueueUserWorkItem(_ => Finished?.Invoke());
        }

        private async Task WriteLoopAsync()
        {
            try
            {
                await foreach (var block in _queue.Reader.ReadAllAsync())
                    await _stream.WriteAsync(block);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "recorder.write failed file={File}", File);
            }
        }

        public void Close()
        {
            Volatile.Write(ref _finishedFired, 1);
            _queue.Writer.TryComplete();
            try { _writer.Wait(TimeSpan.FromSeconds(5)); } catch { }
            try
            {
                long dataBytes = _stream.Length - 44;
                _stream.Position = 0;
                _stream.Write(WavHeader((int)Math.Min(dataBytes, int.MaxValue - 44)));
                _stream.Flush();
            }
            finally
            {
                _stream.Dispose();
            }
        }
    }

    internal static byte[] WavHeader(int dataBytes)
    {
        var h = new byte[44];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(4), 36 + dataBytes);
        "WAVE"u8.CopyTo(h.AsSpan(8));
        "fmt "u8.CopyTo(h.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(20), 1);          // PCM
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(22), 1);          // mono
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), RateHz);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), RateHz * 2);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(34), 16);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(40), dataBytes);
        return h;
    }

    // ---- playback on the air -----------------------------------------

    public bool StartPlayback(RecorderPlayRequest req, out string? error)
    {
        var path = ResolvePath(req.File, out error);
        if (path is null) return false;
        if (!File.Exists(path)) { error = "That recording isn't there any more."; return false; }
        var ingest = _services.GetService(typeof(TxAudioIngest)) as TxAudioIngest;
        var speaker = _services.GetService(typeof(IPreviewAudioSink)) as IPreviewAudioSink;
        if (req.Local)
        {
            if (speaker is null) { error = "The engine's audio output isn't running."; return false; }
        }
        else
        {
            if (ingest is null) { error = "The transmit audio path isn't running."; return false; }
            var mode = RadioFrequencyResolver.TxReceiver(_radio.Snapshot()).Mode;
            if (mode is RxMode.CWL or RxMode.CWU)
            {
                error = "Switch to a voice mode to transmit a recording.";
                return false;
            }
            if (_pipeline.IsFreeDvActive)
            {
                error = "Turn FreeDV off to transmit a recording.";
                return false;
            }
        }
        float[] clip;
        try { clip = WavReader.ReadMono48k(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error = $"Can't read the recording: {ex.Message}";
            return false;
        }
        if (clip.Length < BlockSamples) { error = "The recording is empty."; return false; }
        if (clip.Length > MaxPlaySeconds * RateHz)
        {
            error = $"Recordings up to {MaxPlaySeconds / 60:0} minutes can be transmitted.";
            return false;
        }
        if (req.Local) Shape(clip, 0, normalise: true, peak: LocalPeak);
        else Shape(clip, Math.Clamp(req.GainDb, -20, 20), normalise: !req.Processed);

        lock (_sync)
        {
            if (_playback is not null) { error = "Already transmitting a recording."; return false; }
            if (_recording is { Source: "mic" }) { error = "Stop the recording first."; return false; }
            bool riding = !req.Local && _tx.IsMoxOn;
            if (riding && _tx.MoxOwner is not (MoxSource.UI or MoxSource.Hardware))
            {
                error = "Something else is transmitting.";
                return false;
            }
            var play = new Playback(Path.GetFileName(path), clip, req.Processed, riding, req.Local);
            _playback = play;
            _lastError = null;
            var thread = new Thread(() => { if (play.Local) RunLocal(play, speaker!); else RunPlayback(play, ingest!); })
            {
                IsBackground = true,
                Name = "recorder-playback",
                Priority = ThreadPriority.AboveNormal,
            };
            thread.Start();
            _log.LogInformation("recorder.play.start file={File} local={Local} gainDb={Gain} processed={Processed} riding={Riding}",
                req.File, req.Local, req.GainDb, req.Processed, riding);
            return true;
        }
    }

    public void StopPlayback()
    {
        lock (_sync) _playback?.Cancel();
    }

    /// <summary>Gain, then for a receiver clip a peak level the TX chain is
    /// comfortable with, so recordings of loud and quiet stations go out alike.</summary>
    internal static void Shape(float[] clip, double gainDb, bool normalise, float peak = ReceiverPeak)
    {
        float target = peak;
        float gain = (float)Math.Pow(10, gainDb / 20);
        if (normalise)
        {
            float clipPeak = 0;
            foreach (var v in clip) clipPeak = Math.Max(clipPeak, Math.Abs(v));
            if (clipPeak > 1e-4f) gain *= target / clipPeak;
        }
        for (int i = 0; i < clip.Length; i++)
            clip[i] = Math.Clamp(clip[i] * gain, -1f, 1f);
    }

    private void RunPlayback(Playback play, TxAudioIngest ingest)
    {
        bool keyed = false;
        string? error = null;
        try
        {
            if (!play.Riding)
            {
                if (!_tx.TrySetMox(true, MoxSource.Wav, out error)) return;
                keyed = true;
            }
            var payload = new byte[BlockSamples * 4];
            long start = Stopwatch.GetTimestamp();
            int blocks = (play.Clip.Length + BlockSamples - 1) / BlockSamples;
            const int LeadBlocks = 2;   // keep a little queued ahead, like a sound card
            for (int i = 0; i < blocks; i++)
            {
                if (play.Cancelled) break;
                // The operator (MOX/PTT) or a safety trip took over or dropped the key.
                if (keyed ? !_tx.IsMoxOn || _tx.MoxOwner != MoxSource.Wav : !_tx.IsMoxOn) break;

                long due = start + (long)((i - LeadBlocks) * 0.020 * Stopwatch.Frequency);
                while (Stopwatch.GetTimestamp() < due && !play.Cancelled) Thread.Sleep(1);

                int offset = i * BlockSamples;
                int n = Math.Min(BlockSamples, play.Clip.Length - offset);
                var block = MemoryMarshal.Cast<byte, float>(payload.AsSpan());
                play.Clip.AsSpan(offset, n).CopyTo(block);
                block[n..].Clear();
                ingest.OnMicPcmBytesFromWav(payload, play.Processed);
                play.PositionSamples = offset + n;
            }
            // Let the last queued blocks reach the air before the key drops.
            long end = start + (long)(blocks * 0.020 * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < end && !play.Cancelled) Thread.Sleep(2);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.LogWarning(ex, "recorder.play failed file={File}", play.File);
        }
        finally
        {
            if (keyed && _tx.IsMoxOn && _tx.MoxOwner == MoxSource.Wav)
                _tx.TrySetMox(false, MoxSource.Wav, out _);
            lock (_sync)
            {
                if (ReferenceEquals(_playback, play)) _playback = null;
                if (error is not null) _lastError = error;
            }
            _log.LogInformation("recorder.play.end file={File} error={Error}", play.File, error);
        }
    }

    // To the speakers: the engine's local side-channel, mixed with receive
    // audio into the output device (nothing else uses it in this fork).
    private void RunLocal(Playback play, IPreviewAudioSink speaker)
    {
        bool wasEnabled = speaker.IsEnabled;
        // Kept queued ahead of the output device (its callbacks can take
        // several thousand samples at once); paced by what's actually queued,
        // so it follows the device's own clock instead of a timer.
        const int QueueTarget = 7200;   // 150 ms, under the 16384-sample ring
        try
        {
            speaker.SetPreviewExclusive(true);
            speaker.SetEnabled(true);
            int blocks = (play.Clip.Length + BlockSamples - 1) / BlockSamples;
            for (int i = 0; i < blocks && !play.Cancelled; i++)
            {
                while (speaker.PreviewQueuedSamples > QueueTarget && !play.Cancelled) Thread.Sleep(2);
                int offset = i * BlockSamples;
                int n = Math.Min(BlockSamples, play.Clip.Length - offset);
                speaker.PublishPreview(play.Clip.AsSpan(offset, n), RateHz);
                play.PositionSamples = Math.Max(0, offset + n - speaker.PreviewQueuedSamples);
            }
            // Let what's queued play out.
            var drain = Stopwatch.StartNew();
            while (speaker.PreviewQueuedSamples > 0 && !play.Cancelled && drain.ElapsedMilliseconds < 2000)
                Thread.Sleep(5);
            play.PositionSamples = play.Clip.Length;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "recorder.listen failed file={File}", play.File);
            lock (_sync) _lastError = ex.Message;
        }
        finally
        {
            // Off clears what's still queued, so Stop is immediate.
            speaker.SetPreviewExclusive(false);
            if (!wasEnabled) speaker.SetEnabled(false);
            lock (_sync) if (ReferenceEquals(_playback, play)) _playback = null;
        }
    }

    private sealed class Playback(string file, float[] clip, bool processed, bool riding, bool local)
    {
        public bool Local { get; } = local;
        private volatile bool _cancelled;
        public string File { get; } = file;
        public float[] Clip { get; } = clip;
        public bool Processed { get; } = processed;
        public bool Riding { get; } = riding;
        public volatile int PositionSamples;
        public bool Cancelled => _cancelled;
        public void Cancel() => _cancelled = true;
        public double PositionSeconds => PositionSamples / (double)RateHz;
        public double LengthSeconds => Clip.Length / (double)RateHz;
    }

    public void Dispose()
    {
        StopPlayback();
        StopRecording();
    }
}

/// <summary>Reads PCM 16/24/32-bit or float WAV, any channel count and rate,
/// as 48 kHz mono.</summary>
internal static class WavReader
{
    public static float[] ReadMono48k(string path)
    {
        var data = File.ReadAllBytes(path);
        var span = data.AsSpan();
        if (span.Length < 12 || !span[..4].SequenceEqual("RIFF"u8) || !span.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("not a WAV file");
        int pos = 12, format = 0, channels = 0, rate = 0, bits = 0;
        ReadOnlySpan<byte> pcm = default;
        while (pos + 8 <= span.Length)
        {
            var id = span.Slice(pos, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 4, 4));
            int body = pos + 8;
            if (size < 0) throw new InvalidDataException("bad chunk");
            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                format = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(body, 2));
                channels = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(body + 2, 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(body + 4, 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(body + 14, 2));
                if (format == 0xFFFE && size >= 26)   // WAVE_FORMAT_EXTENSIBLE: the sub-format's first two bytes
                    format = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(body + 24, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                pcm = span.Slice(body, Math.Min(size, span.Length - body));
            }
            pos = body + size + (size & 1);
        }
        if (channels < 1 || rate < 8000 || rate > 384000 || pcm.IsEmpty)
            throw new InvalidDataException("unsupported WAV");
        int bytesPer = bits / 8;
        if (!(format == 1 && bits is 16 or 24 or 32) && !(format == 3 && bits == 32))
            throw new InvalidDataException($"unsupported WAV format {format}/{bits}-bit");
        int frames = pcm.Length / (bytesPer * channels);
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                var s = pcm.Slice((f * channels + c) * bytesPer, bytesPer);
                sum += format == 3 ? BinaryPrimitives.ReadSingleLittleEndian(s)
                    : bits switch
                    {
                        16 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                        24 => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
                        _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
                    };
            }
            float v = sum / channels;
            mono[f] = float.IsFinite(v) ? v : 0;
        }
        return rate == AudioRecorderService.RateHz ? mono : Resample(mono, rate, AudioRecorderService.RateHz);
    }

    // Linear interpolation; good enough for speech going out at 3 kHz bandwidth.
    private static float[] Resample(float[] input, int from, int to)
    {
        int n = (int)((long)input.Length * to / from);
        var output = new float[n];
        double step = (double)from / to;
        for (int i = 0; i < n; i++)
        {
            double x = i * step;
            int k = (int)x;
            double t = x - k;
            float a = input[Math.Min(k, input.Length - 1)];
            float b = input[Math.Min(k + 1, input.Length - 1)];
            output[i] = (float)(a + (b - a) * t);
        }
        return output;
    }
}
