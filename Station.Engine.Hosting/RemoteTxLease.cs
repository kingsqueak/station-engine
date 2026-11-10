// SPDX-License-Identifier: GPL-2.0-or-later

namespace SimpleHPSDR.Server;

internal static class RemoteTxLease
{
    internal const string HeaderName = "X-SimpleHPSDR-Remote-Tx-Lease";

    internal static bool TryGet(HttpContext context, out string leaseId)
    {
        leaseId = context.Request.Headers[HeaderName].ToString();
        return IsValid(leaseId);
    }

    internal static bool IsValid(string? leaseId)
        => leaseId is { Length: 32 }
            && leaseId.All(static c => char.IsAsciiHexDigit(c));
}
