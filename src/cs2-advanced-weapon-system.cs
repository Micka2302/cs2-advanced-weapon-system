using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;
using static AdvancedWeaponSystem.Config;
using static AdvancedWeaponSystem.Weapon;
using static CounterStrikeSharp.API.Core.Listeners;

namespace AdvancedWeaponSystem;

public class AdvancedWeaponSystem : BasePlugin
{
    private const string CanAcquireSignatureName = "CCSPlayer_ItemServices_CanAcquire";
    private const string CanAcquireLinuxSignature = "55 48 89 E5 41 57 41 56 41 55 49 89 CD 41 54 49 89 FC 53 48 89 F3 48 83 EC";

    private MemoryFunctionWithReturn<CCSPlayer_ItemServices, CEconItemView, CCSWeaponBaseVData, AcquireMethod, nint, AcquireResult>? _canAcquireFunc;
    private string _canAcquireSignatureSource = "unresolved";
    private string? _canAcquireGameDataPath;
    private bool _canAcquireHooked;
    private int _restrictionTickCounter;
    private long _debugSequence;
    private readonly Dictionary<string, bool> _cachedQuotaRestrictions = [];
    private readonly Dictionary<string, DateTime> _restrictionNoticeCooldown = [];
    private static readonly TimeSpan RestrictionNoticeInterval = TimeSpan.FromSeconds(2);

    public override string ModuleName => "Advanced Weapon System";
    public override string ModuleVersion => "v13-json-config-gamedata-updater";
    public override string ModuleAuthor => "schwarper";

    public Config Config { get; set; } = new Config();
    public static AdvancedWeaponSystem Instance { get; private set; } = new();

    public override void Load(bool hotReload)
    {
        Instance = this;
        string configDirectory = ConfigLoader.GetConfigDirectory(ModuleDirectory);
        Config = ConfigLoader.Load(ModuleDirectory, Logger);
        Logger.LogInformation("Loading {ModuleName} {ModuleVersion}. hotReload={HotReload} EnableCanAcquireHook={EnableCanAcquireHook} AutoUpdateSignatures={AutoUpdateSignatures}", ModuleName, ModuleVersion, hotReload, Config.EnableCanAcquireHook, Config.AutoUpdateSignatures);

        if (Config.AutoUpdateSignatures)
            GameDataUpdater.DownloadMissingFile(configDirectory, Config.GameDataUpdateUrl, Logger).GetAwaiter().GetResult();

        if (Config.EnableCanAcquireHook)
        {
            try
            {
                _canAcquireFunc = CreateCanAcquireFunction();
                Logger.LogInformation("CanAcquire signature source: {SignatureSource}. gamedata={GameDataPath}", _canAcquireSignatureSource, _canAcquireGameDataPath ?? "none");
                _canAcquireFunc.Hook(OnWeaponCanAcquire, HookMode.Pre);
                _canAcquireHooked = true;
                Logger.LogInformation("CanAcquire hook enabled. Restricted weapons will be blocked before inventory acquisition.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to hook CanAcquire. Inventory fallback will only warn because removing acquired weapons can crash the server.");
            }
        }
        else
        {
            Logger.LogWarning("CanAcquire hook disabled by config. Inventory fallback will only warn because removing acquired weapons can crash the server.");
        }
    }

    public override void Unload(bool hotReload)
    {
        if (_canAcquireHooked && _canAcquireFunc is not null)
        {
            try
            {
                _canAcquireFunc.Unhook(OnWeaponCanAcquire, HookMode.Pre);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Ignoring CanAcquire unhook error.");
            }
        }

        _canAcquireHooked = false;
        _canAcquireFunc = null;
        _cachedQuotaRestrictions.Clear();
        _restrictionNoticeCooldown.Clear();
    }

    [GameEventHandler]
    public HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        try
        {
            CrashDebugLog("WeaponFire enter.");
            if (@event.Userid is not { } player || player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value is not { } activeWeapon)
            {
                CrashDebugLog("WeaponFire skipped: player or active weapon unavailable.");
                return HookResult.Continue;
            }

            string weaponName = Weapon.GetDesignerName(activeWeapon);
            CrashDebugLog("WeaponFire active weapon resolved. player={Player} weapon={Weapon} designer={DesignerName}", player.PlayerName, weaponName, activeWeapon.DesignerName);

            if (!Config.WeaponDatas.TryGetValue(weaponName, out WeaponData? weaponData))
            {
                CrashDebugLog("WeaponFire skipped: no config for weapon={Weapon}", weaponName);
                return HookResult.Continue;
            }

            CrashDebugLog("WeaponFire matched. player={Player} weapon={Weapon} clip={Clip}", player.PlayerName, activeWeapon.DesignerName, activeWeapon.Clip1);

            if (weaponData.UnlimitedClip == true)
            {
                activeWeapon.Clip1 += 1;
                CrashDebugLog("UnlimitedClip applied. player={Player} weapon={Weapon} clip={Clip}", player.PlayerName, activeWeapon.DesignerName, activeWeapon.Clip1);
            }

            if (HasUnlimitedReserve(weaponData))
                RefillReserveAmmo(activeWeapon, weaponData);

            if (weaponData.ReloadAfterShoot == true)
            {
                if (activeWeapon.As<CCSWeaponBase>().VData is not { } weaponVData)
                    return HookResult.Continue;

                uint slot = (uint)weaponVData.GearSlot + 1;
                CrashDebugLog("ReloadAfterShoot switching to knife. player={Player} weapon={Weapon} returnSlot={Slot}", player.PlayerName, activeWeapon.DesignerName, slot);
                player.ExecuteClientCommand("slot3");

                Instance.AddTimer(0.1f, () =>
                {
                    try
                    {
                        if (player.PlayerPawn.Value is null)
                            return;

                        player.ExecuteClientCommand($"slot{slot}");
                        CrashDebugLog("ReloadAfterShoot returned to weapon slot. player={Player} slot={Slot}", player.PlayerName, slot);
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
            CrashDebugLog("EntitySpawn enter. valid={Valid} designer={DesignerName}", entity.IsValid, entity.DesignerName);
            string designerName = entity.DesignerName ?? string.Empty;
            if (!entity.IsValid || !designerName.StartsWith("weapon_"))
            {
                CrashDebugLog("EntitySpawn skipped: not valid weapon. valid={Valid} designer={DesignerName}", entity.IsValid, designerName);
                return;
            }

            CrashDebugLog("EntitySpawn before weapon cast. designer={DesignerName}", designerName);
            CBasePlayerWeapon weapon = entity.As<CBasePlayerWeapon>();
            string weaponName = Weapon.GetDesignerName(weapon);
            CrashDebugLog("EntitySpawn weapon resolved. designer={DesignerName} weapon={Weapon}", designerName, weaponName);
            if (!Config.WeaponDatas.TryGetValue(weaponName, out WeaponData? weaponData))
            {
                CrashDebugLog("EntitySpawn skipped: no config for weapon={Weapon}", weaponName);
                return;
            }

            CrashDebugLog("EntitySpawn before VData access. weapon={Weapon}", weaponName);
            if (entity.As<CCSWeaponBase>().VData is not CCSWeaponBaseVData weaponVData)
            {
                CrashDebugLog("EntitySpawn skipped: VData unavailable. weapon={Weapon}", weaponName);
                return;
            }

            CrashDebugLog("EntitySpawn matched. weapon={Weapon}", designerName);

            if (weaponData.Clip.HasValue)
            {
                CrashDebugLog("Updating MaxClip1. weapon={Weapon} old={OldValue} new={NewValue}", designerName, weaponVData.MaxClip1, weaponData.Clip.Value);
                weaponVData.MaxClip1 = weaponData.Clip.Value;
            }

            if (ResolveReserveAmmo(weaponData, weaponVData) is int reserveAmmo)
            {
                CrashDebugLog("Updating PrimaryReserveAmmoMax. weapon={Weapon} old={OldValue} new={NewValue}", designerName, weaponVData.PrimaryReserveAmmoMax, reserveAmmo);
                weaponVData.PrimaryReserveAmmoMax = reserveAmmo;
            }
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
            CrashDebugLog("TakeDamagePre enter. pawnValid={PawnValid} damage={Damage}", playerPawn.IsValid, info.Damage);
            if (!playerPawn.IsValid)
                return HookResult.Continue;

            CBaseEntity? weapon = info.Ability.Value;
            if (weapon is null || !weapon.IsValid)
            {
                CrashDebugLog("TakeDamagePre skipped: weapon unavailable or invalid.");
                return HookResult.Continue;
            }

            string weaponName = NormalizeDesignerName(weapon.DesignerName);
            if (!weaponName.StartsWith("weapon_"))
            {
                CrashDebugLog("TakeDamagePre skipped: ability is not weapon. designer={DesignerName}", weapon.DesignerName);
                return HookResult.Continue;
            }

            if (!Config.WeaponDatas.TryGetValue(weaponName, out WeaponData? weaponData))
            {
                CrashDebugLog("TakeDamagePre skipped: no config for weapon={Weapon}", weaponName);
                return HookResult.Continue;
            }

            CrashDebugLog("TakeDamagePre matched. weapon={Weapon} damage={Damage} hitgroup={HitGroup}", weaponName, info.Damage, info.GetHitGroup());

            if (weaponData.OnlyHeadshot == true && info.GetHitGroup() != HitGroup_t.HITGROUP_HEAD)
            {
                CrashDebugLog("TakeDamagePre blocked by OnlyHeadshot. weapon={Weapon} hitgroup={HitGroup}", weaponName, info.GetHitGroup());
                return HookResult.Handled;
            }

            float oldDamage = info.Damage;
            SetDamage(info, weaponData);
            CrashDebugLog("TakeDamagePre damage set. weapon={Weapon} oldDamage={OldDamage} newDamage={NewDamage}", weaponName, oldDamage, info.Damage);
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

            CrashDebugLog("OnTick restriction pass start. tickCounter={TickCounter} canAcquireHooked={CanAcquireHooked}", _restrictionTickCounter, _canAcquireHooked);
            UpdateQuotaRestrictionCache();
            CrashDebugLog("OnTick quota cache updated. entries={Entries}", _cachedQuotaRestrictions.Count);

            if (_canAcquireHooked)
                return;

            foreach (CCSPlayerController player in Utilities.GetPlayers())
                EnforcePlayerWeaponRestrictions(player);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unhandled exception in OnTick restriction enforcement.");
        }
    }

    private void UpdateQuotaRestrictionCache()
    {
        CrashDebugLog("QuotaCache clear. previousEntries={Entries}", _cachedQuotaRestrictions.Count);
        _cachedQuotaRestrictions.Clear();

        foreach (CCSPlayerController player in Utilities.GetPlayers())
        {
            if (!player.IsValid)
                continue;

            CrashDebugLog("QuotaCache player scan. player={Player} steamId={SteamId} team={Team}", player.PlayerName, player.SteamID, player.Team);
            foreach ((string weaponName, WeaponData weaponData) in Config.WeaponDatas)
            {
                if ((weaponData.WeaponQuota?.Count ?? 0) == 0 && (weaponData.MapSpecificQuota?.Count ?? 0) == 0)
                    continue;

                try
                {
                    bool isRestricted = IsRestricted(player, weaponName, weaponData, AcquireMethod.Buy);
                    _cachedQuotaRestrictions[GetRestrictionCacheKey(player, weaponName)] = isRestricted;
                    CrashDebugLog("QuotaCache result. player={Player} weapon={Weapon} restricted={Restricted}", player.PlayerName, weaponName, isRestricted);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to update cached quota restriction for player {Player} weapon {WeaponName}.", player.PlayerName, weaponName);
                }
            }
        }
    }

    private void EnforcePlayerWeaponRestrictions(CCSPlayerController player)
    {
        try
        {
            CrashDebugLog("InventoryFallback enter. player={Player} valid={Valid}", player.PlayerName, player.IsValid);
            if (!player.IsValid || !player.PlayerPawn.IsValid)
            {
                CrashDebugLog("InventoryFallback skipped: invalid player or pawn. player={Player}", player.PlayerName);
                return;
            }

            if (player.PlayerPawn.Value?.As<CCSPlayerPawn>() is not CCSPlayerPawn pawn || !pawn.IsValid)
            {
                CrashDebugLog("InventoryFallback skipped: pawn unavailable. player={Player}", player.PlayerName);
                return;
            }

            if (pawn.WeaponServices is not CPlayer_WeaponServices weaponServices)
            {
                CrashDebugLog("InventoryFallback skipped: weapon services unavailable. player={Player}", player.PlayerName);
                return;
            }

            List<(CBasePlayerWeapon Weapon, string WeaponName)> restrictedWeapons = [];
            foreach (CHandle<CBasePlayerWeapon> weaponHandle in weaponServices.MyWeapons)
            {
                if (!weaponHandle.IsValid || weaponHandle.Value is not CBasePlayerWeapon weapon || !weapon.IsValid)
                    continue;

                string weaponName = Weapon.GetDesignerName(weapon);
                CrashDebugLog("InventoryFallback weapon scan. player={Player} weapon={Weapon}", player.PlayerName, weaponName);
                if (!Config.WeaponDatas.TryGetValue(weaponName, out WeaponData? weaponData))
                    continue;

                if (!IsRestricted(player, weaponName, weaponData, AcquireMethod.PickUp))
                    continue;

                restrictedWeapons.Add((weapon, weaponName));
            }

            foreach ((CBasePlayerWeapon weapon, string weaponName) in restrictedWeapons)
            {
                CrashDebugLog("Restricted weapon detected after acquisition. player={Player} weapon={Weapon}. Not removing it to avoid WriteEnterPVS crash; verify CanAcquire hook is enabled and blocking this path.", player.PlayerName, weaponName);
                NotifyRestriction(player, weaponName);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to enforce restrictions for player {Player}.", player.PlayerName);
        }
    }

    public HookResult OnWeaponCanAcquire(DynamicHook hook)
    {
        long seq = NextDebugSequence();
        try
        {
            CrashDebugLog("CanAcquire[{Seq}] enter.", seq);

            CrashDebugLog("CanAcquire[{Seq}] before GetParam<CEconItemView>(1).", seq);
            CEconItemView econItem = hook.GetParam<CEconItemView>(1);
            CrashDebugLog("CanAcquire[{Seq}] after GetParam<CEconItemView>(1).", seq);

            CrashDebugLog("CanAcquire[{Seq}] before ItemDefinitionIndex read.", seq);
            ushort defIndex = econItem.ItemDefinitionIndex;
            CrashDebugLog("CanAcquire[{Seq}] defIndex={DefIndex}.", seq, defIndex);

            if (!WeaponIndexToName.TryGetValue(defIndex, out string? weaponName))
            {
                CrashDebugLog("CanAcquire[{Seq}] continue: unknown defIndex={DefIndex}.", seq, defIndex);
                return HookResult.Continue;
            }

            CrashDebugLog("CanAcquire[{Seq}] weaponName={Weapon}.", seq, weaponName);

            if (!Config.WeaponDatas.TryGetValue(weaponName, out WeaponData? weaponData))
            {
                CrashDebugLog("CanAcquire[{Seq}] continue: no config for weapon={Weapon}.", seq, weaponName);
                return HookResult.Continue;
            }

            CrashDebugLog("CanAcquire[{Seq}] config found. weapon={Weapon} block={Block} quotas={Quotas} mapQuotas={MapQuotas} flags={Flags}", seq, weaponName, weaponData.BlockUsing, weaponData.WeaponQuota?.Count ?? 0, weaponData.MapSpecificQuota?.Count ?? 0, weaponData.AdminFlagsToIgnoreBlockUsing?.Count ?? 0);

            CrashDebugLog("CanAcquire[{Seq}] before GetParam<CCSPlayer_ItemServices>(0).", seq);
            CCSPlayer_ItemServices itemServices = hook.GetParam<CCSPlayer_ItemServices>(0);
            CrashDebugLog("CanAcquire[{Seq}] after GetParam<CCSPlayer_ItemServices>(0).", seq);

            CrashDebugLog("CanAcquire[{Seq}] before pawn/controller resolve.", seq);
            if (itemServices.Pawn.Value?.Controller.Value?.As<CCSPlayerController>() is not CCSPlayerController player)
            {
                CrashDebugLog("CanAcquire[{Seq}] continue: player unavailable.", seq);
                return HookResult.Continue;
            }

            CrashDebugLog("CanAcquire[{Seq}] player resolved. player={Player} steamId={SteamId} team={Team} isBot={IsBot}", seq, player.PlayerName, player.SteamID, player.Team, player.IsBot);

            CrashDebugLog("CanAcquire[{Seq}] before GetParam<CCSWeaponBaseVData>(2).", seq);
            CCSWeaponBaseVData? weaponVData = hook.GetParam<CCSWeaponBaseVData>(2);
            CrashDebugLog("CanAcquire[{Seq}] after GetParam<CCSWeaponBaseVData>(2). hasVData={HasVData}", seq, weaponVData is not null);

            CrashDebugLog("CanAcquire[{Seq}] before GetParam<AcquireMethod>(3).", seq);
            AcquireMethod acquireMethod = hook.GetParam<AcquireMethod>(3);
            CrashDebugLog("CanAcquire[{Seq}] acquireMethod={AcquireMethod}.", seq, acquireMethod);

            CrashDebugLog("CanAcquire[{Seq}] before GetParam<nint>(4).", seq);
            nint unknown = hook.GetParam<nint>(4);
            CrashDebugLog("CanAcquire[{Seq}] unknownParam4=0x{Unknown:X}.", seq, unknown);

            CrashDebugLog("CanAcquire[{Seq}] before restriction decision.", seq);
            bool isRestricted = IsRestrictedForCanAcquire(seq, player, weaponName, weaponData, acquireMethod);
            CrashDebugLog("CanAcquire[{Seq}] restriction decision={Restricted}.", seq, isRestricted);

            if (!isRestricted)
            {
                CrashDebugLog("CanAcquire[{Seq}] continue: allowed. weapon={Weapon} method={AcquireMethod}", seq, weaponName, acquireMethod);
                return HookResult.Continue;
            }

            CrashDebugLog("CanAcquire[{Seq}] blocked. player={Player} weapon={Weapon} method={AcquireMethod}", seq, player.PlayerName, weaponName, acquireMethod);
            CrashDebugLog("CanAcquire[{Seq}] before SetReturn NotAllowedByProhibition.", seq);
            hook.SetReturn(AcquireResult.NotAllowedByProhibition);
            CrashDebugLog("CanAcquire[{Seq}] after SetReturn; returning Handled.", seq);
            return HookResult.Handled;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unhandled exception in OnWeaponCanAcquire. seq={Seq}", seq);
            CrashDebugLog("CanAcquire[{Seq}] managed exception caught. message={Message}", seq, ex.Message);
        }

        CrashDebugLog("CanAcquire[{Seq}] returning Continue after exception.", seq);
        return HookResult.Continue;
    }

    private bool IsRestrictedForCanAcquire(long seq, CCSPlayerController player, string weaponName, WeaponData weaponData, AcquireMethod acquireMethod)
    {
        CrashDebugLog("CanAcquire[{Seq}] restriction: start. weapon={Weapon}", seq, weaponName);
        string[] flags = [.. weaponData.AdminFlagsToIgnoreBlockUsing];
        CrashDebugLog("CanAcquire[{Seq}] restriction: flags count={Flags}", seq, flags.Length);
        if (flags.Length > 0 && !player.IsBot && AdminManager.PlayerHasPermissions(new SteamID(player.SteamID), flags))
        {
            CrashDebugLog("CanAcquire[{Seq}] restriction: admin bypass true.", seq);
            return false;
        }

        if (weaponData.BlockUsing == true && (weaponData.IgnorePickUpFromBlockUsing != false || acquireMethod != AcquireMethod.PickUp))
        {
            CrashDebugLog("CanAcquire[{Seq}] restriction: blocked by BlockUsing.", seq);
            return true;
        }

        if ((weaponData.WeaponQuota?.Count ?? 0) == 0 && (weaponData.MapSpecificQuota?.Count ?? 0) == 0)
        {
            CrashDebugLog("CanAcquire[{Seq}] restriction: no quota rules.", seq);
            return false;
        }

        string key = GetRestrictionCacheKey(player, weaponName);
        bool found = _cachedQuotaRestrictions.TryGetValue(key, out bool isRestricted);
        CrashDebugLog("CanAcquire[{Seq}] restriction: cache key={Key} found={Found} restricted={Restricted}", seq, key, found, isRestricted);
        return found && isRestricted;
    }

    private static void RefillReserveAmmo(CBasePlayerWeapon activeWeapon, WeaponData weaponData)
    {
        if (activeWeapon.As<CCSWeaponBase>().VData is not CCSWeaponBaseVData weaponVData)
            return;

        int reserveTarget = ResolveReserveAmmo(weaponData, weaponVData) ?? weaponVData.PrimaryReserveAmmoMax;
        if (reserveTarget <= 0)
            reserveTarget = Math.Max(weaponVData.PrimaryReserveAmmoMax, 1);

        int currentReserve = activeWeapon.ReserveAmmo[0];
        if (currentReserve < reserveTarget)
            activeWeapon.ReserveAmmo[0] = reserveTarget;
    }

    private static string GetRestrictionCacheKey(CCSPlayerController player, string weaponName)
    {
        return $"{player.SteamID}:{weaponName}";
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

    private static string NormalizeDesignerName(string? designerName)
    {
        if (string.IsNullOrWhiteSpace(designerName))
            return "weapon_unknown";

        return designerName.Trim().ToLowerInvariant();
    }

    [Conditional("AWS_TRACE")]
    private void CrashDebugLog(string message, params object?[] args)
    {
        Logger.LogInformation("[CrashDebug] " + message, args);
    }

    private long NextDebugSequence()
    {
        return System.Threading.Interlocked.Increment(ref _debugSequence);
    }

    private MemoryFunctionWithReturn<CCSPlayer_ItemServices, CEconItemView, CCSWeaponBaseVData, AcquireMethod, nint, AcquireResult> CreateCanAcquireFunction()
    {
        return new MemoryFunctionWithReturn<CCSPlayer_ItemServices, CEconItemView, CCSWeaponBaseVData, AcquireMethod, nint, AcquireResult>(GetCanAcquireSignature());
    }

    private string GetCanAcquireSignature()
    {
        if (TryGetPluginSignature(CanAcquireSignatureName, out string? signature, out string source, out string? gameDataPath))
        {
            _canAcquireSignatureSource = source;
            _canAcquireGameDataPath = gameDataPath;
            return signature!;
        }

        _canAcquireGameDataPath = gameDataPath;
        if (OperatingSystem.IsLinux())
        {
            _canAcquireSignatureSource = $"{source}; built-in-linux-fallback";
            return CanAcquireLinuxSignature;
        }

        throw new InvalidOperationException($"Missing local gamedata signature {CanAcquireSignatureName} for this platform. gamedata={gameDataPath}");
    }

    private bool TryGetPluginSignature(string signatureName, out string? signature, out string source, out string? gameDataPath)
    {
        signature = null;
        gameDataPath = ResolvePluginGameDataPath();
        source = $"plugin-gamedata-missing:{gameDataPath}";

        if (!File.Exists(gameDataPath))
            return false;

        try
        {
            string json = File.ReadAllText(gameDataPath);
            PluginGameData? gameData = JsonSerializer.Deserialize<PluginGameData>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            if (gameData?.Signatures is null || !gameData.Signatures.TryGetValue(signatureName, out PluginSignature? entry))
            {
                source = $"plugin-gamedata-entry-missing:{signatureName}";
                return false;
            }

            string? platformSignature = OperatingSystem.IsLinux() ? entry.Linux : entry.Windows;
            if (string.IsNullOrWhiteSpace(platformSignature))
            {
                source = $"plugin-gamedata-empty:{signatureName}:{(OperatingSystem.IsLinux() ? "linux" : "windows")}";
                return false;
            }

            signature = NormalizeSignature(platformSignature);
            source = $"plugin-gamedata:{signatureName}:{(OperatingSystem.IsLinux() ? "linux" : "windows")}";
            return true;
        }
        catch (Exception ex)
        {
            source = $"plugin-gamedata-error:{ex.GetType().Name}:{ex.Message}";
            return false;
        }
    }

    private string ResolvePluginGameDataPath()
    {
        return Path.Combine(ConfigLoader.GetConfigDirectory(ModuleDirectory), "gamedata.json");
    }

    private static string NormalizeSignature(string signature)
    {
        return string.Join(' ', signature.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed class PluginGameData
    {
        public Dictionary<string, PluginSignature> Signatures { get; set; } = [];
    }

    private sealed class PluginSignature
    {
        public string? Linux { get; set; }
        public string? Windows { get; set; }
        public string? ManagedSignature { get; set; }
        public string? Notes { get; set; }
    }
}
