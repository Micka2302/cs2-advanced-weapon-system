using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using static AdvancedWeaponSystem.Config;
using static AdvancedWeaponSystem.Weapon;
using static CounterStrikeSharp.API.Core.Listeners;

namespace AdvancedWeaponSystem;

public class AdvancedWeaponSystem : BasePlugin, IPluginConfig<Config>
{
    public override string ModuleName => "Advanced Weapon System";
    public override string ModuleVersion => "1.11";
    public override string ModuleAuthor => "schwarper";

    public Config Config { get; set; } = new Config();
    private int _restrictionTickCounter;
    private readonly Dictionary<string, DateTime> _restrictionNoticeCooldown = new();
    private static readonly TimeSpan RestrictionNoticeInterval = TimeSpan.FromSeconds(2);


    public override void Load(bool hotReload)
    {
    }

    public override void Unload(bool hotReload)
    {
        _restrictionNoticeCooldown.Clear();
    }

    public void OnConfigParsed(Config config)
    {
        Config = SanitizeConfig(config);
    }

    [GameEventHandler]
    public HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        try
        {
            if (@event.Userid is not { } player || player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value is not { } activeWeapon)
                return HookResult.Continue;

            if (!TryGetWeaponData(GetDesignerName(activeWeapon.DesignerName), out WeaponData weaponData))
                return HookResult.Continue;

            if (weaponData.UnlimitedClip == true)
                activeWeapon.Clip1 += 1;

            if (HasUnlimitedReserve(weaponData))
                RefillReserveAmmo(activeWeapon, weaponData);

            if (weaponData.ReloadAfterShoot == true)
            {
                if (activeWeapon.As<CCSWeaponBase>().VData is not { } weaponVData)
                    return HookResult.Continue;

                player.ExecuteClientCommand("slot3");

                AddTimer(0.1f, () =>
                {
                    try
                    {
                        if (player.PlayerPawn.Value is null)
                            return;

                        player.ExecuteClientCommand($"slot{(uint)weaponVData.GearSlot + 1}");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogDebug(ex, "Ignoring reload-after-shoot command error.");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unhandled exception in OnWeaponFire.");
        }

        return HookResult.Continue;
    }

    [ListenerHandler<OnEntitySpawned>]
    public void OnEntitySpawned(CEntityInstance entity)
    {
        try
        {
            string? designerName = entity.DesignerName;
            if (!entity.IsValid || string.IsNullOrWhiteSpace(designerName) || !designerName.StartsWith("weapon_", StringComparison.Ordinal))
                return;

            if (!TryGetWeaponData(GetDesignerName(designerName), out WeaponData weaponData))
                return;

            if (entity.As<CCSWeaponBase>().VData is not CCSWeaponBaseVData weaponVData)
                return;

            if (weaponData.Clip is int clip)
                weaponVData.MaxClip1 = Math.Max(clip, 0);

            if (ResolveReserveAmmo(weaponData, weaponVData) is int reserveAmmo)
                weaponVData.PrimaryReserveAmmoMax = reserveAmmo;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unhandled exception in OnEntitySpawned.");
        }
    }

    [ListenerHandler<OnPlayerTakeDamagePre>]
    public HookResult OnPlayerTakeDamagePre(CCSPlayerPawn playerPawn, CTakeDamageInfo info)
    {
        try
        {
            if (!playerPawn.IsValid)
                return HookResult.Continue;

            CBaseEntity? weapon = info.Ability.Value;
            if (weapon is null || !weapon.IsValid)
                return HookResult.Continue;

            string weaponName = GetDesignerName(weapon.DesignerName);
            if (!weaponName.StartsWith("weapon_", StringComparison.Ordinal))
                return HookResult.Continue;

            if (!TryGetWeaponData(weaponName, out WeaponData weaponData))
                return HookResult.Continue;

            if (weaponData.OnlyHeadshot == true && info.GetHitGroup() != HitGroup_t.HITGROUP_HEAD)
                return HookResult.Handled;

            SetDamage(info, weaponData);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unhandled exception in OnPlayerTakeDamagePre.");
        }

        return HookResult.Continue;
    }

    [ListenerHandler<OnTick>]
    public void OnTick()
    {
        try
        {
            _restrictionTickCounter++;
            if (_restrictionTickCounter % 16 != 0)
                return;

            foreach (CCSPlayerController player in Utilities.GetPlayers())
                EnforcePlayerWeaponRestrictions(player);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unhandled exception in OnTick restriction enforcement.");
        }
    }

    private bool TryGetWeaponData(string weaponName, out WeaponData weaponData)
    {
        weaponData = null!;
        if (string.IsNullOrWhiteSpace(weaponName) || Config.WeaponDatas is null)
            return false;

        if (!Config.WeaponDatas.TryGetValue(weaponName, out WeaponData? configuredData) || configuredData is null)
            return false;

        weaponData = configuredData;
        return true;
    }

    private void EnforcePlayerWeaponRestrictions(CCSPlayerController player)
    {
        try
        {
            if (!player.IsValid || !player.PlayerPawn.IsValid)
                return;

            if (player.PlayerPawn.Value?.As<CCSPlayerPawn>() is not CCSPlayerPawn pawn || !pawn.IsValid)
                return;

            if (pawn.WeaponServices is not CPlayer_WeaponServices weaponServices)
                return;

            List<(CBasePlayerWeapon Weapon, string WeaponName)> restrictedWeapons = [];
            foreach (CHandle<CBasePlayerWeapon> weaponHandle in weaponServices.MyWeapons)
            {
                if (!weaponHandle.IsValid || weaponHandle.Value is not CBasePlayerWeapon weapon || !weapon.IsValid)
                    continue;

                string weaponName = GetDesignerName(weapon.DesignerName);
                if (!TryGetWeaponData(weaponName, out WeaponData weaponData))
                    continue;

                if (!IsRestricted(player, weaponName, weaponData, AcquireMethod.PickUp))
                    continue;

                restrictedWeapons.Add((weapon, weaponName));
            }

            foreach ((CBasePlayerWeapon weapon, string weaponName) in restrictedWeapons)
            {
                try
                {
                    pawn.RemovePlayerItem(weapon);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to remove restricted weapon {WeaponName} from player {Player}.", weaponName, player.PlayerName);
                    continue;
                }

                NotifyRestriction(player, weaponName);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to enforce restrictions for player {Player}.", player.PlayerName);
        }
    }

    private void NotifyRestriction(CCSPlayerController player, string weaponName)
    {
        if (player.IsBot)
            return;

        string key = $"{player.SteamID}:{weaponName}";
        DateTime now = DateTime.UtcNow;
        if (_restrictionNoticeCooldown.TryGetValue(key, out DateTime lastNotice) && now - lastNotice < RestrictionNoticeInterval)
            return;

        _restrictionNoticeCooldown[key] = now;
        try
        {
            Localizer.ForPlayer(player, "You cannot use this weapon", weaponName);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to send restriction message for weapon {WeaponName} to player {Player}.", weaponName, player.PlayerName);
        }
    }

    private static Config SanitizeConfig(Config? config)
    {
        Config sanitized = config ?? new Config();
        sanitized.WeaponDatas ??= [];

        Dictionary<string, WeaponData> normalizedWeaponData = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string configuredWeaponName, WeaponData configuredData) in sanitized.WeaponDatas)
        {
            WeaponData data = configuredData ?? new WeaponData();
            string weaponName = string.IsNullOrWhiteSpace(configuredWeaponName) ? data.Weapon : configuredWeaponName;
            if (string.IsNullOrWhiteSpace(weaponName))
                continue;

            string normalizedWeaponName = weaponName.Trim().ToLowerInvariant();
            data.Weapon = normalizedWeaponName;
            data.AdminFlagsToIgnoreBlockUsing = data.AdminFlagsToIgnoreBlockUsing?
                .Where(static flag => !string.IsNullOrWhiteSpace(flag))
                .Select(static flag => flag.Trim())
                .ToList() ?? [];
            data.WeaponQuota = data.WeaponQuota?
                .Where(static kvp => kvp.Key >= 0 && kvp.Value >= 0)
                .ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value) ?? [];
            data.MapSpecificQuota = SanitizeMapSpecificQuota(data.MapSpecificQuota);

            normalizedWeaponData[normalizedWeaponName] = data;
        }

        sanitized.WeaponDatas = normalizedWeaponData;
        return sanitized;
    }

    private static Dictionary<string, Dictionary<int, int>> SanitizeMapSpecificQuota(Dictionary<string, Dictionary<int, int>>? mapSpecificQuota)
    {
        Dictionary<string, Dictionary<int, int>> normalized = new(StringComparer.OrdinalIgnoreCase);
        if (mapSpecificQuota is null)
            return normalized;

        foreach ((string mapName, Dictionary<int, int> quota) in mapSpecificQuota)
        {
            if (string.IsNullOrWhiteSpace(mapName) || quota is null)
                continue;

            Dictionary<int, int> cleanQuota = quota
                .Where(static kvp => kvp.Key >= 0 && kvp.Value >= 0)
                .ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value);

            if (cleanQuota.Count == 0)
                continue;

            normalized[mapName.Trim().ToLowerInvariant()] = cleanQuota;
        }

        return normalized;
    }

    private static void RefillReserveAmmo(CBasePlayerWeapon activeWeapon, WeaponData weaponData)
    {
        if (activeWeapon.As<CCSWeaponBase>().VData is not CCSWeaponBaseVData weaponVData)
            return;

        int reserveTarget = ResolveReserveAmmo(weaponData, weaponVData) ?? weaponVData.PrimaryReserveAmmoMax;
        if (reserveTarget <= 0)
            reserveTarget = Math.Max(weaponVData.PrimaryReserveAmmoMax, 1);

        try
        {
            int currentReserve = activeWeapon.ReserveAmmo[0];
            if (currentReserve < reserveTarget)
                activeWeapon.ReserveAmmo[0] = reserveTarget;
        }
        catch
        {
            // Ignore invalid reserve ammo slots to avoid runtime crashes.
        }
    }

    private static string GetDesignerName(string? designerName)
    {
        if (string.IsNullOrWhiteSpace(designerName))
            return "weapon_unknown";

        return designerName.Trim().ToLowerInvariant();
    }
}



