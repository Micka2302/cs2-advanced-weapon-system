using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using System.Globalization;
using static AdvancedWeaponSystem.Config;

namespace AdvancedWeaponSystem;

public static class Weapon
{
    public static bool TryGetDefIndex(string weaponName, out ushort defIndex)
    {
        return weaponsList.TryGetValue(weaponName, out defIndex);
    }

    public static string GetDesignerName(CBasePlayerWeapon weapon)
    {
        try
        {
            return weapon.GetVData<CCSWeaponBaseVData>()?.Name ?? "weapon_unknown";
        }
        catch
        {
            return "weapon_unknown";
        }
    }

    public static bool HasUnlimitedReserve(WeaponData weaponData)
    {
        return weaponData.UnlimitedMagazines == true || weaponData.UnlimitedAmmo == true;
    }

    public static int? ResolveReserveAmmo(WeaponData weaponData, CCSWeaponBaseVData weaponVData)
    {
        if (weaponData.Magazines is int magazines)
            return Math.Max(magazines, 0);

        if (weaponData.Ammo is not int ammo)
            return null;

        if (ShouldConvertLegacyBulletAmmoToMagazines(ammo, weaponVData))
            return ConvertBulletsToMagazineCount(ammo, weaponVData.MaxClip1);

        return Math.Max(ammo, 0);
    }

    private static bool ShouldConvertLegacyBulletAmmoToMagazines(int configuredAmmo, CCSWeaponBaseVData weaponVData)
    {
        int maxClip = weaponVData.MaxClip1;
        int defaultReserve = weaponVData.PrimaryReserveAmmoMax;

        if (maxClip <= 0 || defaultReserve <= 0)
            return false;

        bool gameUsesMagazineReserve = defaultReserve < maxClip;
        bool configuredAmmoLooksLikeBulletCount = configuredAmmo > maxClip;

        return gameUsesMagazineReserve && configuredAmmoLooksLikeBulletCount;
    }

    private static int ConvertBulletsToMagazineCount(int configuredAmmo, int maxClip)
    {
        if (configuredAmmo <= 0)
            return 0;

        if (maxClip <= 0)
            return configuredAmmo;

        return (int)Math.Ceiling((double)configuredAmmo / maxClip);
    }

    public static void SetDamage(CTakeDamageInfo info, WeaponData weaponData)
    {
        if (string.IsNullOrWhiteSpace(weaponData.Damage))
            return;

        string damageValue = weaponData.Damage.Trim();
        float oldDamage = info.Damage;

        if (float.TryParse(damageValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float directValue))
        {
            info.Damage = Math.Max(0f, directValue);
            return;
        }

        if (damageValue.Length < 2)
            return;

        char operation = damageValue[0];
        if (!float.TryParse(damageValue[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            return;

        float newDamage = operation switch
        {
            '+' => oldDamage + value,
            '-' => oldDamage - value,
            '*' => oldDamage * value,
            '/' => value != 0 ? oldDamage / value : oldDamage,
            _ => oldDamage
        };

        if (!float.IsFinite(newDamage))
            return;

        info.Damage = Math.Max(0f, newDamage);
    }

    public static bool IsRestricted(CCSPlayerController player, string weaponName, WeaponData weaponData, AcquireMethod acquireMethod)
    {
        string[] flags = weaponData.AdminFlagsToIgnoreBlockUsing?
            .Where(static flag => !string.IsNullOrWhiteSpace(flag))
            .Select(static flag => flag.Trim())
            .ToArray() ?? [];

        if (flags.Length > 0 && !player.IsBot && AdminManager.PlayerHasPermissions(new SteamID(player.SteamID), flags))
            return false;

        if (weaponData.BlockUsing == true && (weaponData.IgnorePickUpFromBlockUsing != false || acquireMethod != AcquireMethod.PickUp))
            return true;

        if ((weaponData.WeaponQuota?.Count ?? 0) > 0 || (weaponData.MapSpecificQuota?.Count ?? 0) > 0)
        {
            if (!TryGetDefIndex(weaponName, out ushort defIndex))
                return false;

            List<CCSPlayerController> players = [.. Utilities.GetPlayers().Where(p => p.Team == player.Team)];
            int playerCount = players.Count;

            string currentMap = (Server.MapName ?? string.Empty).ToLowerInvariant();

            // 🔹 Ako postoji MapSpecificQuota za ovu mapu, koristi ga
            if (weaponData.MapSpecificQuota is { Count: > 0 } mapSpecificQuota &&
                mapSpecificQuota.TryGetValue(currentMap, out Dictionary<int, int>? mapQuota) &&
                mapQuota is { Count: > 0 })
            {
                int maxWeapons = mapQuota
                    .Where(kvp => playerCount >= kvp.Key)
                    .Select(kvp => kvp.Value)
                    .DefaultIfEmpty(0)
                    .Max();

                int weaponsCount = players.Sum(p => p.PlayerPawn.Value?.WeaponServices?.MyWeapons.Sum(w => Count(defIndex, w, p)) ?? 0);

                return weaponsCount >= maxWeapons;
            }

            // Inače koristi globalni WeaponQuota
            Dictionary<int, int> globalQuota = weaponData.WeaponQuota ?? [];
            int maxGlobal = globalQuota
                .Where(kvp => playerCount >= kvp.Key)
                .Select(kvp => kvp.Value)
                .DefaultIfEmpty(0)
                .Max();

            int globalCount = players.Sum(p => p.PlayerPawn.Value?.WeaponServices?.MyWeapons.Sum(w => Count(defIndex, w, p)) ?? 0);

            return globalCount >= maxGlobal;
        }

        return false;
    }

    private static int Count(ushort defIndex, CHandle<CBasePlayerWeapon> weapon, CCSPlayerController player)
    {
        if (weapon.Value?.AttributeManager.Item.ItemDefinitionIndex is not ushort index || index != defIndex)
            return 0;

        if (player.PlayerPawn.Value?.WeaponServices is not CPlayer_WeaponServices weaponServices)
            return 0;

        int total = 0;

        const int HE_SLOT = 13;
        const int FLASH_SLOT = 14;
        const int SMOKE_SLOT = 15;
        const int MOLOTOV_SLOT = 16;
        const int DECOY_SLOT = 17;

        switch (index)
        {
            case (ushort)ItemDefinition.FRAG_GRENADE:
            case (ushort)ItemDefinition.HIGH_EXPLOSIVE_GRENADE:
                total += ReadAmmoSlot(weaponServices, HE_SLOT);
                break;

            case (ushort)ItemDefinition.FLASHBANG:
                total += ReadAmmoSlot(weaponServices, FLASH_SLOT);
                break;

            case (ushort)ItemDefinition.SMOKE_GRENADE:
                total += ReadAmmoSlot(weaponServices, SMOKE_SLOT);
                break;

            case (ushort)ItemDefinition.MOLOTOV:
            case (ushort)ItemDefinition.INCENDIARY_GRENADE:
                total += ReadAmmoSlot(weaponServices, MOLOTOV_SLOT);
                break;

            case (ushort)ItemDefinition.DECOY_GRENADE:
                total += ReadAmmoSlot(weaponServices, DECOY_SLOT);
                break;

            default:
                total++;
                break;
        }

        return total;
    }

    private static int ReadAmmoSlot(CPlayer_WeaponServices weaponServices, int slot)
    {
        try
        {
            if (slot < 0)
                return 1;

            return Math.Max((int)weaponServices.Ammo[slot], 1);
        }
        catch
        {
            return 1;
        }
    }

    private static readonly Dictionary<string, ushort> weaponsList = new()
    {
        { "weapon_m4a1", (ushort)ItemDefinition.M4A4 },
        { "weapon_m4a1_silencer", (ushort)ItemDefinition.M4A1_S },
        { "weapon_famas", (ushort)ItemDefinition.FAMAS },
        { "weapon_aug", (ushort)ItemDefinition.AUG },
        { "weapon_ak47", (ushort)ItemDefinition.AK_47 },
        { "weapon_galilar", (ushort)ItemDefinition.GALIL_AR },
        { "weapon_sg556", (ushort)ItemDefinition.SG_553 },
        { "weapon_scar20", (ushort)ItemDefinition.SCAR_20 },
        { "weapon_awp", (ushort)ItemDefinition.AWP },
        { "weapon_ssg08", (ushort)ItemDefinition.SSG_08 },
        { "weapon_g3sg1", (ushort)ItemDefinition.G3SG1 },
        { "weapon_mp9", (ushort)ItemDefinition.MP9 },
        { "weapon_mp7", (ushort)ItemDefinition.MP7 },
        { "weapon_mp5sd", (ushort)ItemDefinition.MP5_SD },
        { "weapon_ump45", (ushort)ItemDefinition.UMP_45 },
        { "weapon_p90", (ushort)ItemDefinition.P90 },
        { "weapon_bizon", (ushort)ItemDefinition.PP_BIZON },
        { "weapon_mac10", (ushort)ItemDefinition.MAC_10 },
        { "weapon_usp_silencer", (ushort)ItemDefinition.USP_S },
        { "weapon_hkp2000", (ushort)ItemDefinition.P2000 },
        { "weapon_glock", (ushort)ItemDefinition.GLOCK_18 },
        { "weapon_elite", (ushort)ItemDefinition.DUAL_BERETTAS },
        { "weapon_p250", (ushort)ItemDefinition.P250 },
        { "weapon_fiveseven", (ushort)ItemDefinition.FIVE_SEVEN },
        { "weapon_cz75a", (ushort)ItemDefinition.CZ75_AUTO },
        { "weapon_tec9", (ushort)ItemDefinition.TEC_9 },
        { "weapon_revolver", (ushort)ItemDefinition.R8_REVOLVER },
        { "weapon_deagle", (ushort)ItemDefinition.DESERT_EAGLE },
        { "weapon_nova", (ushort)ItemDefinition.NOVA },
        { "weapon_xm1014", (ushort)ItemDefinition.XM1014 },
        { "weapon_mag7", (ushort)ItemDefinition.MAG_7 },
        { "weapon_sawedoff", (ushort)ItemDefinition.SAWED_OFF },
        { "weapon_m249", (ushort)ItemDefinition.M249 },
        { "weapon_negev", (ushort)ItemDefinition.NEGEV },
        { "weapon_taser", (ushort)ItemDefinition.ZEUS_X27 },
        { "weapon_hegrenade", (ushort)ItemDefinition.HIGH_EXPLOSIVE_GRENADE },
        { "weapon_molotov", (ushort)ItemDefinition.MOLOTOV },
        { "weapon_incgrenade", (ushort)ItemDefinition.INCENDIARY_GRENADE },
        { "weapon_smokegrenade", (ushort)ItemDefinition.SMOKE_GRENADE },
        { "weapon_flashbang", (ushort)ItemDefinition.FLASHBANG },
        { "weapon_decoy", (ushort)ItemDefinition.DECOY_GRENADE }
    };

    public static readonly Dictionary<ushort, string> WeaponIndexToName = weaponsList
        .ToDictionary(x => x.Value, x => x.Key);
}
