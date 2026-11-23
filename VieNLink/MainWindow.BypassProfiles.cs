namespace VieNLink;

public partial class MainWindow
{
    // ---------- память удачных конфигов обходов ----------

    private bool TryApplyBestZapretProfile()
    {
        var best = BypassProfileStore.Ranked("zapret").FirstOrDefault();
        if (best is null)
        {
            return false;
        }

        var index = ZapretService.Presets
            .Select((preset, position) => (preset, position))
            .Where(item => item.preset.Name.Equals(best.Name, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.position)
            .DefaultIfEmpty(best.StrategyIndex)
            .First();
        index = Math.Clamp(index, 0, ZapretService.Presets.Count - 1);
        _settings.ZapretPreset = index;
        _settings.ZapretBlockQuic = best.BlockQuic;
        UpdateZapretStrategyInfo(index);
        if (ZapretBlockQuicCheck is not null)
        {
            ZapretBlockQuicCheck.IsChecked = best.BlockQuic;
        }

        SaveSettings();
        AppendLog($"Zapret: сохранённый конфиг «{best.Name}» (качество {best.Quality})");
        AppLog.Write("bypass", $"Zapret: применён сохранённый конфиг «{best.Name}», качество {best.Quality}, использований {best.Uses}");
        return true;
    }

    private bool TryApplyBestDesyncProfile()
    {
        var best = BypassProfileStore.Ranked("desync").FirstOrDefault();
        if (best is null)
        {
            return false;
        }

        var index = DesyncService.Strategies
            .Select((strategy, position) => (strategy, position))
            .Where(item => item.strategy.Name.Equals(best.Name, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.position)
            .DefaultIfEmpty(best.StrategyIndex)
            .First();
        index = Math.Clamp(index, 0, DesyncService.Strategies.Count - 1);
        _settings.DesyncStrategy = index;
        _settings.DesyncBlockQuic = best.BlockQuic;
        UpdateDesyncStrategyInfo(index);
        if (DesyncBlockQuicCheck is not null)
        {
            DesyncBlockQuicCheck.IsChecked = best.BlockQuic;
        }

        SaveSettings();
        AppendLog($"Свой обход DPI: сохранённый конфиг «{best.Name}» (качество {best.Quality})");
        AppLog.Write("bypass", $"Свой DPI: применён сохранённый конфиг «{best.Name}», качество {best.Quality}, использований {best.Uses}");
        return true;
    }

    private bool TryApplyBestTunProfile(out bool desyncEngine)
    {
        desyncEngine = false;
        var best = BypassProfileStore.Ranked("tun")
            .Concat(BypassProfileStore.Ranked("tun-desync"))
            .OrderByDescending(profile => profile.Quality)
            .ThenByDescending(profile => profile.LastSuccessUtc)
            .FirstOrDefault();
        if (best is null)
        {
            return false;
        }

        if (best.Mode.Equals("tun-desync", StringComparison.OrdinalIgnoreCase))
        {
            var index = DesyncService.Strategies
                .Select((strategy, position) => (strategy, position))
                .Where(item => item.strategy.Name.Equals(best.Name, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.position)
                .DefaultIfEmpty(best.StrategyIndex)
                .First();
            index = Math.Clamp(index, 0, DesyncService.Strategies.Count - 1);
            _settings.DesyncStrategy = index;
            _settings.DesyncBlockQuic = best.BlockQuic;
            UpdateDesyncStrategyInfo(index);
            if (DesyncBlockQuicCheck is not null)
            {
                DesyncBlockQuicCheck.IsChecked = best.BlockQuic;
            }

            SaveSettings();
            desyncEngine = true;
            AppendLog($"TUN-обход: сохранённый WinDivert-конфиг «{best.Name}» (качество {best.Quality})");
            AppLog.Write("bypass", $"TUN: применён сохранённый WinDivert-конфиг «{best.Name}», качество {best.Quality}, использований {best.Uses}");
            return true;
        }

        var tunIndex = TlsFragmentForwarder.Strategies
            .Select((strategy, position) => (strategy, position))
            .Where(item => item.strategy.Name.Equals(best.Name, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.position)
            .DefaultIfEmpty(best.StrategyIndex)
            .First();
        tunIndex = Math.Clamp(tunIndex, 0, TlsFragmentForwarder.Strategies.Count - 1);
        _settings.TunBypassStrategy = tunIndex;
        _settings.TunBypassBlockQuic = best.BlockQuic;
        SelectTunBypassStrategyRadio(tunIndex);
        if (TunBypassBlockQuicCheck is not null)
        {
            TunBypassBlockQuicCheck.IsChecked = best.BlockQuic;
        }

        SaveSettings();
        AppendLog($"TUN-обход: сохранённый конфиг «{best.Name}» (качество {best.Quality})");
        AppLog.Write("bypass", $"TUN: применён сохранённый конфиг «{best.Name}», качество {best.Quality}, использований {best.Uses}");
        return true;
    }

    private static BypassProfile ZapretProfile(ZapretProbeResult result, bool gameMode, bool blockQuic) => new()
    {
        Mode = "zapret",
        StrategyIndex = result.Index,
        Name = result.Name,
        GameMode = gameMode,
        BlockQuic = blockQuic,
        Quality = BypassProfileStore.Score(
            result.Ok, result.Total, result.AverageMs,
            result.YoutubeOk, result.TelegramOk, result.VideoOk, result.DiscordOk),
        Ok = result.Ok,
        Total = result.Total,
        YoutubeOk = result.YoutubeOk,
        TelegramOk = result.TelegramOk,
        DiscordOk = result.DiscordOk,
        VideoOk = result.VideoOk,
        AverageMs = result.AverageMs,
    };

    private static BypassProfile DesyncProfile(
        DesyncProbeResult result,
        bool blockQuic,
        string mode) => new()
    {
        Mode = mode,
        StrategyIndex = result.Index,
        Name = result.Name,
        BlockQuic = blockQuic,
        Quality = BypassProfileStore.Score(
            result.Ok, result.Total, result.AverageMs,
            result.YoutubeOk, result.TelegramOk, result.VideoOk, discord: false),
        Ok = result.Ok,
        Total = result.Total,
        YoutubeOk = result.YoutubeOk,
        TelegramOk = result.TelegramOk,
        VideoOk = result.VideoOk,
        AverageMs = result.AverageMs,
    };

    private static BypassProfile TunProfile(DesyncProbeResult result, bool blockQuic) =>
        DesyncProfile(result, blockQuic, "tun");

    private static BypassProfile StartedProfile(string mode, int index, string name, bool gameMode, bool blockQuic) => new()
    {
        Mode = mode,
        StrategyIndex = index,
        Name = name,
        GameMode = gameMode,
        BlockQuic = blockQuic,
        Quality = 30,
        Ok = 1,
        Total = 1,
    };
}
