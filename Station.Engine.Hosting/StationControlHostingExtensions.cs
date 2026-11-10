// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using SimpleHPSDR.Server.Cat;
using SimpleHPSDR.Server.Tci;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace SimpleHPSDR.Server;

/// <summary>Registers the engine-owned TCI and CAT station-control transports.</summary>
public static class StationControlHostingExtensions
{
    public static IServiceCollection AddTciServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<TciOptions>>().Value;
            return TciHostingExtensions.ResolveTciListener(
                options.Enabled,
                options.BindAddress,
                options.Port);
        });
        services.AddSingleton<TciConfigStore>();
        services.AddSingleton<TransverterSettingsStore>();
        services.AddSingleton<SpotManager>();
        services.AddSingleton<TciServer>();
        services.AddHostedService(sp => sp.GetRequiredService<TciServer>());
        services.AddSingleton<TciManagementService>();
        services.AddSingleton<HfAutoConfigStore>();
        services.AddSingleton<HfAutoService>();
        services.AddHostedService(sp => sp.GetRequiredService<HfAutoService>());
        return services;
    }

    public static IServiceCollection AddCatServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<CatConfigStore>();
        services.AddSingleton<CatServer>();
        services.AddHostedService(sp => sp.GetRequiredService<CatServer>());
        services.AddSingleton<CatManagementService>();
        services.AddSingleton<CatSerialConfigStore>();
        services.AddSingleton<CatSerialService>();
        services.AddHostedService(sp => sp.GetRequiredService<CatSerialService>());
        return services;
    }

}
