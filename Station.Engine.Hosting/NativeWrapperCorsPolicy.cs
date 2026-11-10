// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Microsoft.AspNetCore.Cors.Infrastructure;

namespace SimpleHPSDR.Server;

/// <summary>
/// Cross-origin policy for the installed Android/iOS wrappers and the signed,
/// static SimpleHPSDR PWA. Origins are fixed product surfaces; arbitrary public web
/// origins remain outside this policy.
/// </summary>
public static class NativeWrapperCorsPolicy
{
    public const string Name = "SimpleHPSDRNativeWrapper";
    public const string AndroidOrigin = "http://localhost";
    public const string IosOrigin = "capacitor://localhost";
    // SimpleHPSDR fork: SimpleHPSDR's hosted web app is no longer allowed.
    public const string HostedPwaOrigin = "https://app.zeussdr.com";

    public static bool IsAllowedOrigin(string origin) =>
        origin is AndroidOrigin or IosOrigin;

    public static void Configure(CorsPolicyBuilder policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy
            .SetIsOriginAllowed(IsAllowedOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    }
}
