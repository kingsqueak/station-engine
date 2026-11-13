// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using SimpleHPSDR.Dsp.Wdsp;
using SimpleHPSDR.Protocol1;

namespace SimpleHPSDR.Server;

public sealed record StationEngineHostingOptions(
    bool NativeAudioOutputEnabled = false,
    string? P2AutoConnectEndpoint = null,
    IReadOnlyList<string>? LanHttpsUrls = null,
    string? GodsEyeDatabasePath = null,
    bool ReceiveOnly = false);

/// <summary>Registers the complete standalone station-engine runtime.</summary>
public static class StationEngineHostingExtensions
{
    /// <summary>
    /// Adds engine-owned stores, services, stream transports, and hosted
    /// lifecycles. Product integration ports are always bound to their null
    /// implementations.
    /// </summary>
    public static IServiceCollection AddStationEngine(
        this IServiceCollection services,
        StationEngineHostingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new StationEngineHostingOptions();

        services.AddSingleton(new StationReceiveOnlyPolicy(options.ReceiveOnly));
        services.AddHttpClient();


        services.AddSingleton(provider => new StationEngineCapabilitiesService(
            provider.GetRequiredService<IConfiguration>(),
            options.LanHttpsUrls));
        // Windows Firewall status/apply for the Settings control — same
        // service registration as the product host, so the engine's
        // /api/system/windows-firewall routes behave identically.
        services.AddSingleton<IWindowsFirewallService, WindowsFirewallService>();
        // StationEngine is the process that opens the HPSDR UDP sockets, so it is
        // the executable the firewall rule has to name. Nothing granted that rule
        // automatically before: the only auto-apply lived in SimpleHPSDR.Server.Hosting,
        // which only SimpleHPSDR.Host (the retired dev bench) references.
        //
        // These two are inert until resolved, so they are safe to register here.
        // WindowsFirewallStartupGrant is NOT: it shells out to netsh and can create
        // a real firewall rule. Registering it in this shared helper would run it in
        // every integration test that builds a host — including on the self-hosted
        // Windows runners, where the rules would accumulate. The engine executable
        // opts in explicitly; see AddWindowsFirewallStartupGrant.
        services.AddSingleton<WindowsFirewallGrantStore>();
        services.AddSingleton<IWindowsFirewallElevationHelper>(sp =>
            OperatingSystem.IsWindows()
                ? new ScheduledTaskFirewallElevationHelper(
                    sp.GetRequiredService<ILogger<ScheduledTaskFirewallElevationHelper>>())
                : NullFirewallElevationHelper.Instance);
        services.AddSingleton<IProductStreamSource>(_ => NullProductStreamSource.Instance);
        // Real (not null) sink: the SPA's clientErrorBeacon prefers the /ws
        // diagnostic frame, so a null sink would silently discard uncaught
        // webview errors exactly when the engine is the only API origin.
        services.AddSingleton<ClientLogRateLimiter>();
        services.AddSingleton<EngineClientDiagnosticSink>();
        services.AddSingleton<IClientDiagnosticSink>(sp =>
            sp.GetRequiredService<EngineClientDiagnosticSink>());
        services.AddSingleton<RadeModemService>();
        services.AddSingleton<CompositeAudioModemPort>();
        services.AddSingleton<IAudioModemPort>(sp =>
            sp.GetRequiredService<CompositeAudioModemPort>());
        services.AddSingleton<ITxAudioPreviewProcessor, NullTxAudioPreviewProcessor>();
        services.AddSingleton<IInitialTxAudioConfigSource, NullInitialTxAudioConfigSource>();

        services.AddSingleton<DspSettingsStore>();
        services.AddSingleton<PaSettingsStore>();
        services.AddSingleton<FilterPresetStore>();
        services.AddSingleton<PreferredRadioStore>();
        services.AddSingleton<SMeterCalibrationStore>();
        services.AddSingleton(sp => new PsSettingsStore(
            sp.GetRequiredService<ILogger<PsSettingsStore>>(),
            PrefsDbPath.EngineGet()));
        services.AddSingleton<RadioStateStore>();
        // Workspace layouts: the store defaults to the product simplehpsdr-prefs.db
        // (where existing desktop installs keep their layouts), so the engine
        // must pass its own database explicitly — attach-session layouts live
        // in the engine-owned prefs DB like every other engine store.
        services.AddSingleton(sp => new LayoutStore(
            sp.GetRequiredService<ILogger<LayoutStore>>(),
            PrefsDbPath.EngineGet()));
        services.AddSingleton<CwSettingsStore>();
        services.AddSingleton<CwIdSettingsStore>();
        services.AddSingleton<AntennaSettingsStore>();
        services.AddSingleton<VnaSweepStore>();
        services.AddSingleton<Hl2VnaSweepHardware>();
        services.AddSingleton<RadioBridgeVnaSweepHardware>();
        services.AddSingleton<ConnectedRadioVnaSweepHardware>();
        services.AddSingleton<NanoVnaSweepHardware>();
        services.AddSingleton<VnaHardwareRouter>();
        services.AddSingleton<IVnaSweepHardware>(sp => sp.GetRequiredService<VnaHardwareRouter>());
        services.AddSingleton<VnaService>();
        services.AddSingleton<AudioSettingsStore>();
        services.AddSingleton<Nr3ModelStore>();
        services.AddSingleton<CfcPresetStore>();
        services.AddSingleton<Hl2GpioSettingsStore>();
        services.AddSingleton<BandMemoryStore>();
        services.AddSingleton<BandStackStore>();
        services.AddSingleton<StationFavoriteStore>();
        services.AddSingleton<RfFilterSettingsStore>();
        services.AddSingleton<PttSettingsStore>();
        services.AddSingleton<SerialPttSettingsStore>();
        services.AddSingleton<AudioDeviceSettingsStore>();
        services.AddSingleton<RadioSpeakerSettingsStore>();
        services.AddSingleton<TxMonitorSettingsStore>();
        services.AddSingleton<TxFidelityPolicyStore>();
        services.AddSingleton<BandPlanStore>();
        services.AddSingleton<BandPrefsStore>();
        services.AddSingleton<BandPlanService>();
        services.AddSingleton<IBandPlanService>(sp => sp.GetRequiredService<BandPlanService>());

        // Operator UI preference stores (theme / display / toolbar / NR
        // disclosure / operator identity / layout pins). These deliberately
        // keep their default PrefsDbPath.Get() product database — the same
        // file the product host has always used for these collections — so an
        // operator's saved look-and-feel follows them between the desktop host
        // and a standalone engine on the same machine, with no migration.
        services.AddSingleton<DisplaySettingsStore>();
        services.AddSingleton<OperatorIdentityStore>();
        // Digital workspace prefs (per-mode FT8/FT4/WSPR settings + the Auto-CQ
        // ack stamp). Same collections the desktop host uses, so the rows follow
        // the operator between hosts on the same machine.

        services.AddSingleton<TxIqRing>();
        services.AddSingleton<ITxIqSource>(sp => sp.GetRequiredService<TxIqRing>());
        services.AddSingleton<RxAudioRing>();
        services.AddSingleton<IRxAudioSource>(sp => sp.GetRequiredService<RxAudioRing>());
        services.AddSingleton<RxAudioMuteState>();

        services.AddSingleton<StreamingHub>();
        services.AddSingleton<RadioService>();
        services.AddSingleton<RadioReclaimService>();
        services.AddSingleton<
            SimpleHPSDR.Protocol1.Discovery.IRadioDiscovery,
            SimpleHPSDR.Protocol1.Discovery.RadioDiscoveryService>();
        services.AddSingleton<
            SimpleHPSDR.Protocol2.Discovery.IRadioDiscovery,
            SimpleHPSDR.Protocol2.Discovery.RadioDiscoveryService>();
        services.AddSingleton<IRadioDiscoveryExtension, NullRadioDiscoveryExtension>();

        services.AddSingleton<RadioSpeakerAudioSink>();
        services.AddSingleton<SaturnSpeakerAudioSink>();
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<NativeAudioSink>(
            sp,
            options.NativeAudioOutputEnabled));
        services.AddSingleton<WebSocketAudioSink>();
        services.AddSingleton<IRxAudioSink>(sp => sp.GetRequiredService<RadioSpeakerAudioSink>());
        services.AddSingleton<IRxAudioSink>(sp => sp.GetRequiredService<SaturnSpeakerAudioSink>());
        services.AddSingleton<IRxAudioSink>(sp => sp.GetRequiredService<WebSocketAudioSink>());
        // Register the inert sink too: a runtime ASIO opt-in reuses this same
        // DSP destination without constructing a second capture/output session.
        services.AddSingleton<IRxAudioSink>(sp => sp.GetRequiredService<NativeAudioSink>());
        services.AddSingleton<IPreviewAudioSink>(sp => sp.GetRequiredService<NativeAudioSink>());

        services.AddSingleton<WdspWisdomInitializer>();
        services.AddSingleton<WisdomBootstrapService>();
        services.AddSingleton<DspPipelineService>(sp =>
        {
            Func<TxAudioIngest?> txIngestFactory = () => sp.GetService<TxAudioIngest>();
            return ActivatorUtilities.CreateInstance<DspPipelineService>(sp, txIngestFactory);
        });
        services.AddProtocol2ConnectionServices();
        services.AddSingleton<FrequencyCalibrationService>();
        services.AddSingleton<PaCalibrationService>();
        services.AddSingleton<ImdMeasureService>();
        services.AddSingleton<TxService>();
        services.AddSingleton<TuneCarrierCommandCoordinator>();
        // The pipeline builds one TX-gated modem around the registered
        // IAudioModemPort. Only TxAudioIngest receives that gate. Passing it
        // here fills the public constructor's modem argument; the rest of the
        // constructor is resolved from the container.
        services.AddSingleton<TxAudioIngest>(sp =>
            ActivatorUtilities.CreateInstance<TxAudioIngest>(
                sp, sp.GetRequiredService<DspPipelineService>().TxAudioModem));
        services.AddSingleton<TxDiagnosticsService>();
        services.AddSingleton<TxAudioIngestStartup>();
        services.AddSingleton<TxMicMeterService>();
        services.AddSingleton<TxMetersService>();
        services.AddSingleton<TxTuneDriver>();
        services.AddSingleton<CwSidetoneSource>(sp =>
        {
            var source = new CwSidetoneSource();
            var settings = sp.GetRequiredService<CwSettingsStore>().Get();
            source.SetPitchHz(settings.SidetoneHz);
            source.SetGainDb(settings.SidetoneGainDb);
            return source;
        });
        services.AddSingleton<CwEngine>();
        services.AddSingleton<CwIdService>();
        services.AddSingleton<AudioRecorderService>();
        services.AddSingleton<CwDecoderService>();
        services.AddSingleton<ExternalPttService>();
        services.AddHl2IoBoard();
        services.AddSingleton<SerialPttService>();
        services.AddSingleton<NativeMicCapture>();
        services.AddSingleton<VirtualCableTxCapture>();
        services.AddSingleton<IAsioSessionFactory, AsioSessionFactory>();
        services.AddSingleton<NativeHostAudioCoordinator>();

        // Each hosted registration aliases the singleton used by endpoints
        // and other engine services, so there is exactly one instance of each.
        // Native sources start after the core pipeline and therefore stop
        // before it. This prevents an asynchronous device-open callback from
        // creating a preview DSP engine after shutdown has already begun.
        services.AddHostedService(sp => sp.GetRequiredService<SaturnSpeakerAudioSink>());
        services.AddHostedService(sp => sp.GetRequiredService<WisdomBootstrapService>());
        // Push persisted display settings into the DSP before DspPipelineService
        // (or any later connection worker) starts — mirrors the product host's
        // registration order so the DSP-applied subset (TX display calibration,
        // FFT/wideband/frame-rate/decimation/waterfall settings) is live from
        // boot instead of silently running at defaults until the first edit.
        services.AddHostedService<DisplaySettingsApplyService>();
        services.AddHostedService(sp => sp.GetRequiredService<DspPipelineService>());
        if (!string.IsNullOrWhiteSpace(options.P2AutoConnectEndpoint))
        {
            services.AddSingleton(new P2AutoConnectOptions(options.P2AutoConnectEndpoint));
            services.AddSingleton<P2AutoConnectService>();
            services.AddHostedService(sp =>
                sp.GetRequiredService<P2AutoConnectService>());
        }
        services.AddHostedService(sp => sp.GetRequiredService<TxAudioIngestStartup>());
        services.AddHostedService(sp => sp.GetRequiredService<TxMicMeterService>());
        services.AddHostedService(sp => sp.GetRequiredService<TxMetersService>());
        services.AddHostedService(sp => sp.GetRequiredService<TxTuneDriver>());
        services.AddHostedService(sp => sp.GetRequiredService<CwEngine>());
        services.AddHostedService(sp => sp.GetRequiredService<CwDecoderService>());
        services.AddHostedService<PsAutoAttenuateService>();
        services.AddHostedService(sp => sp.GetRequiredService<ExternalPttService>());
        services.AddHostedService(sp => sp.GetRequiredService<SerialPttService>());
        services.AddHostedService(sp => sp.GetRequiredService<NativeAudioSink>());
        services.AddHostedService(sp => sp.GetRequiredService<NativeMicCapture>());
        services.AddHostedService(sp => sp.GetRequiredService<VirtualCableTxCapture>());
        services.AddHostedService(sp => sp.GetRequiredService<NativeHostAudioCoordinator>());

        services.AddTciServices();
        services.AddCatServices();

        return services;
    }

    /// <summary>
    /// Opt in to the automatic, once-per-executable Windows Firewall grant.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <c>AddStationEngine</c>. The grant shells out to
    /// netsh and can create a real firewall rule, so it must not run inside the
    /// integration tests that build a host through the shared registration — on the
    /// self-hosted Windows runners those rules would persist and accumulate. Only
    /// the StationEngine executable calls this.
    /// </remarks>
    public static IServiceCollection AddWindowsFirewallStartupGrant(this IServiceCollection services)
    {
        services.AddHostedService<WindowsFirewallStartupGrant>();
        return services;
    }
}
