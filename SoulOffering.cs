using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.Components;
using ExileCore2.Shared;
using ExileCore2.Shared.Cache;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Helpers;
using System.Windows.Forms;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using RectangleF = ExileCore2.Shared.RectangleF;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using System;

namespace SoulOffering;

public class SoulOffering : BaseSettingsPlugin<SoulOfferingSettings>
{
    private bool _isActive = false;
    private const string SkeletonPath = "Metadata/Monsters/Skeletons/PlayerSummoned/SkeletonClericPlayerSummoned_";
    private readonly Stopwatch _weaponSwapTimer = Stopwatch.StartNew();
    private readonly Stopwatch _castTimer = Stopwatch.StartNew();
    private int _activeWeaponSetIndex;
    private bool _hasInfusionBuff;

    private void LogPluginMessage(string message)
    {
        if (Settings.EnableLogging)
        {
            LogMessage(message);
        }
    }

    private void LogPluginError(string error)
    {
        if (Settings.EnableLogging)
        {
            LogError(error);
        }
    }

    private bool IsInSafeZone()
    {
        var area = GameController.Area.CurrentArea;
        return area?.IsHideout == true || area?.IsTown == true;
    }

    private bool AnyHostileMobsInRange(float range)
    {
        return GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster]
            .Any(x => x?.GetComponent<Monster>() != null &&
                     x.IsValid &&
                     x.IsHostile &&
                     x.IsAlive &&
                     !x.IsHidden &&
                     x.DistancePlayer <= range);
    }

    // State machine
    private enum SkillState
    {
        Idle,
        WaitingForWeaponSwap,
        MovingCursor,
        WaitingForCastCheck,
        RetryingCast,
        SwappingBack
    };

    public override void AreaChange(AreaInstance area)
    {
        base.AreaChange(area);
        GameController.PluginBridge.SaveMethod("SoulOffering.IsActive", () => _isActive);
        LogPluginMessage("SoulOffering.IsActive registered in PluginBridge (via AreaChange)");
    }

    private SkillState _currentState = SkillState.Idle;
    private Entity _targetSkeleton = null;

    public override bool Initialise()
    {
        Settings.WeaponSwapKey.OnValueChanged += () => Input.RegisterKey(Settings.WeaponSwapKey);
        Settings.SoulOfferingKey.OnValueChanged += () => Input.RegisterKey(Settings.SoulOfferingKey);
        Input.RegisterKey(Settings.WeaponSwapKey);
        Input.RegisterKey(Settings.SoulOfferingKey);

        LogPluginMessage("Soul Offering initialized");
        return true;
    }

    private void UpdatePlayerState()
    {
        if (!Settings.Enable.Value) return;

        if (GameController.Player.TryGetComponent<Stats>(out var stats))
        {
            _activeWeaponSetIndex = stats.ActiveWeaponSetIndex;
        }

        _hasInfusionBuff = false;
        if (GameController.Player.TryGetComponent<Buffs>(out var buffComp))
        {
            // Check for infusion buff and ensure it has some duration left
            var buffList = buffComp.BuffsList;
            if (buffList != null)
            {
                var infusionBuff = buffList.FirstOrDefault(b => b.Name == "infusion");
                if (infusionBuff != null && infusionBuff.Timer > 0.1) // Only count buff if it has meaningful duration
                {
                    _hasInfusionBuff = true;
                    LogPluginMessage($"Found Infusion buff with {infusionBuff.Timer:F1}s remaining");
                }
            }
        }
    }

    private Entity GetNearestSkeleton()
    {
        var player = GameController.Player;
        if (player == null) return null;

        return GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster]
            .Where(x => x != null && x.Path != null &&
                       x.Path.Contains(SkeletonPath) &&
                       x.IsAlive &&
                       x.DistancePlayer < 100)
            .OrderBy(x => x.DistancePlayer)
            .FirstOrDefault();
    }

    private Vector2 GetEntityScreenPosition(Entity entity)
    {
        if (entity?.GetComponent<Render>() is not { } render) return Vector2.Zero;

        var entityPos = render.Pos;
        var camera = GameController.Game.IngameState.Camera;
        var window = GameController.Window.GetWindowRectangleTimeCache;
        var screenPos = camera.WorldToScreen(entityPos);

        return window.TopLeft + screenPos;
    }

    private async Task MoveCursorSmoothly(Vector2 targetPos)
    {
        var currentPos = Input.ForceMousePosition;
        var distance = Vector2.Distance(currentPos, targetPos);
        var steps = Math.Clamp((int)(distance / 15), 8, 20);

        var random = new Random();
        for (var i = 0; i < steps; i++)
        {
            var t = (i + 1) / (float)steps;
            var randomOffset = new Vector2(
                ((float)random.NextDouble() * 2.5f) - 1.25f,
                ((float)random.NextDouble() * 2.5f) - 1.25f
            );
            var nextPos = Vector2.Lerp(currentPos, targetPos, t) + randomOffset;
            Input.SetCursorPos(nextPos);
            await Task.Delay(random.Next(4, 8));
        }

        Input.SetCursorPos(targetPos);
        await Task.Delay(25);
    }

    private void SwapWeapon()
    {
        Input.KeyDown(Settings.WeaponSwapKey.Value);
        Input.KeyUp(Settings.WeaponSwapKey.Value);
        _weaponSwapTimer.Restart();
        LogPluginMessage("Weapon swap performed");
    }

    private async Task<bool> CastSoulOffering()
    {
        _targetSkeleton = GetNearestSkeleton();
        if (_targetSkeleton == null)
        {
            LogPluginMessage("No valid skeleton found");
            return false;
        }

        var targetPos = GetEntityScreenPosition(_targetSkeleton);
        if (targetPos == Vector2.Zero)
        {
            LogPluginMessage("Invalid target position");
            return false;
        }

        await MoveCursorSmoothly(targetPos);

        // Extra small delay to ensure cursor is settled
        await Task.Delay(25);

        try
        {
            var key = Settings.SoulOfferingKey.Value;
            LogPluginMessage($"Starting key press sequence for key: {key}");

            Input.KeyDown(key);
            LogPluginMessage("KeyDown sent");

            await Task.Delay(50);

            Input.KeyUp(key);
            LogPluginMessage("KeyUp sent");

            await Task.Delay(1000);
        }
        catch (Exception ex)
        {
            LogPluginError($"Error during key press: {ex.Message}");
            return false;
        }

        _isActive = false;
        _castTimer.Restart();
        LogPluginMessage("Soul Offering cast at skeleton position");
        return true;
    }
        
    private bool IsPluginActive(string pluginName)
    {
        var method = GameController.PluginBridge.GetMethod<Func<bool>>($"{pluginName}.IsActive");
        return method?.Invoke() ?? false;
    }
    private bool ShouldExecute(out string state)
    {
        if (!Settings.Enable.Value)
        {
            state = "Plugin is disabled";
            return false;
        }

        if (!GameController.Window.IsForeground())
        {
            state = "Game window is not focused";
            return false;
        }
        
        if (IsPluginActive("AutoBlink"))
        {
            state = "Paused: AutoBlink is active";
            return false;
        }

        if (Settings.DisableInSafeZones.Value && IsInSafeZone())
        {
            state = $"Player is in a safe zone ({GameController.Area.CurrentArea.Name})";
            return false;
        }

        // Check if any UI panels are open
        if (GameController.Game.IngameState.IngameUi.InventoryPanel.IsVisible)
        {
            state = "Inventory is open";
            return false;
        }

        if (GameController.IngameState.IngameUi.ChatTitlePanel.IsVisible)
        {
            state = "Chat is open";
            return false;
        }

        if (GameController.IngameState.IngameUi.OpenLeftPanel.IsVisible)
        {
            state = "Left panel is open";
            return false;
        }

        if (GameController.IngameState.IngameUi.OpenRightPanel.IsVisible)
        {
            state = "Right panel is open";
            return false;
        }

        // Check for fullscreen panels (like options menu)
        if (GameController.IngameState.IngameUi.FullscreenPanels.Any(p => p.IsVisible))
        {
            state = "Fullscreen panel is open";
            return false;
        }

        // Check for large panels
        if (GameController.IngameState.IngameUi.LargePanels.Any(p => p.IsVisible))
        {
            state = "Large panel is open";
            return false;
        }

        if (GameController.Game.IsEscapeState)
        {
            state = "Game menu is open";
            return false;
        }

        if (AnyHostileMobsInRange(60f))
        {
            state = "Hostile mobs within 60 units - pausing for safety";
            return false;
        }

        // Check player life component
        if (GameController.Player.TryGetComponent<Life>(out var lifeComp))
        {
            if (lifeComp.CurHP <= 0)
            {
                state = "Player is dead";
                return false;
            }
        }

        // Check for grace period
        if (GameController.Player.TryGetComponent<Buffs>(out var buffComp))
        {
            if (buffComp.HasBuff("grace_period"))
            {
                state = "Grace period is active";
                return false;
            }
        }

        state = "Ready";
        return true;
    }

    public override async void Tick()
    {
        if (!ShouldExecute(out string state))
        {
            _currentState = SkillState.Idle;
            _isActive = false;
            _targetSkeleton = null;
            return;
        }

        UpdatePlayerState();

        // In Idle state, always check buff status
        if (_currentState == SkillState.Idle)
        {
            UpdatePlayerState(); // Refresh buff status
            if (!_hasInfusionBuff)
            {
                LogPluginMessage($"No Infusion buff detected (rechecking after combat), starting sequence. Current weapon set: {_activeWeaponSetIndex}");
                _isActive = true;

                if (_activeWeaponSetIndex == 0)
                {
                    SwapWeapon();
                    _currentState = SkillState.WaitingForWeaponSwap;
                    LogPluginMessage("Swapping weapons");
                }
                else
                {
                    _currentState = SkillState.MovingCursor;
                    LogPluginMessage("Moving cursor to target");
                }
                return;
            }
            // If we have the buff, just stay idle
            return;
        }

        switch (_currentState)
        {
            case SkillState.WaitingForWeaponSwap:
                if (_weaponSwapTimer.ElapsedMilliseconds >= Settings.WeaponSwapDelay.Value)
                {
                    _currentState = SkillState.MovingCursor;
                    LogPluginMessage("Weapon swap complete - Moving cursor");
                }
                break;

            case SkillState.MovingCursor:
                var success = await CastSoulOffering();
                if (success)
                {
                    _currentState = SkillState.WaitingForCastCheck;
                    LogPluginMessage("Cast complete - Checking result");
                }
                else
                {
                    _currentState = SkillState.RetryingCast;
                    LogPluginMessage("Cast failed - Will retry");
                }
                break;

            case SkillState.WaitingForCastCheck:
                if (_castTimer.ElapsedMilliseconds >= Settings.CastDelay.Value)
                {
                    // Try checking for buff multiple times with small delays
                    bool buffFound = false;
                    for (int i = 0; i < 3 && !buffFound; i++)
                    {
                        UpdatePlayerState();
                        if (_hasInfusionBuff)
                        {
                            buffFound = true;
                            break;
                        }
                        await Task.Delay(100);
                    }

                    if (buffFound)
                    {
                        if (_activeWeaponSetIndex == 1)
                        {
                            SwapWeapon();
                            _currentState = SkillState.SwappingBack;
                            LogPluginMessage("Buff acquired - Swapping back");
                        }
                        else
                        {
                            _currentState = SkillState.Idle;
                            _isActive = false;
                            LogPluginMessage("Buff acquired - Returning to idle");
                        }
                    }
                    else
                    {
                        LogPluginMessage("No buff detected after multiple checks - Retrying cast");
                        _currentState = SkillState.RetryingCast;
                    }
                }
                break;

            case SkillState.RetryingCast:
                success = await CastSoulOffering();
                if (success)
                {
                    _currentState = SkillState.WaitingForCastCheck;
                    LogPluginMessage("Retry cast complete - Checking result");
                }
                break;

            case SkillState.SwappingBack:
                if (_weaponSwapTimer.ElapsedMilliseconds >= Settings.WeaponSwapDelay.Value)
                {
                    _currentState = SkillState.Idle;
                    _isActive = false;
                    _targetSkeleton = null;
                    LogPluginMessage("Sequence complete");
                }
                break;
        }
    }
}
