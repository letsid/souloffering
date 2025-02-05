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
using InputHumanizer.Input;

namespace SoulOffering;

public class SoulOffering : BaseSettingsPlugin<SoulOfferingSettings>
{
    private bool _isActive = false;
    private const string SkeletonPath = "Metadata/Monsters/Skeletons/PlayerSummoned/SkeletonClericPlayerSummoned_";
    private readonly Stopwatch _weaponSwapTimer = Stopwatch.StartNew();
    private readonly Stopwatch _castTimer = Stopwatch.StartNew();
    private int _activeWeaponSetIndex;
    private bool _hasInfusionBuff;
    private IInputController _inputController;

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

    private SkillState _currentState = SkillState.Idle;
    private Entity _targetSkeleton = null;

    public override bool Initialise()
    {
        Settings.WeaponSwapKey.OnValueChanged += () => Input.RegisterKey(Settings.WeaponSwapKey);
        Settings.SoulOfferingKey.OnValueChanged += () => Input.RegisterKey(Settings.SoulOfferingKey);
        Input.RegisterKey(Settings.WeaponSwapKey);
        Input.RegisterKey(Settings.SoulOfferingKey);

        GameController.PluginBridge.SaveMethod("SoulOffering.IsActive", () => _isActive);
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
            var buffList = buffComp.BuffsList;
            if (buffList != null)
            {
                var infusionBuff = buffList.FirstOrDefault(b => b.Name == "infusion");
                if (infusionBuff != null && infusionBuff.Timer > 0.1)
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

    private async Task MoveCursorSmoothly(Vector2 targetPos, IInputController inputController)
    {
        await inputController.MoveMouse(targetPos);
        // Small delay after movement
        await Task.Delay(25);
    }

    private async Task<bool> SwapWeapon(IInputController inputController, bool isInitialSwap = false)
    {
        try
        {
            await inputController.KeyDown(Settings.WeaponSwapKey.Value);
            await inputController.KeyUp(Settings.WeaponSwapKey.Value);
            
            // Only wait 1060ms if this is the initial swap to weapon set 1
            if (isInitialSwap)
            {
                await Task.Delay(1060);
            }
            
            _weaponSwapTimer.Restart();
            LogPluginMessage($"Weapon swap performed {(isInitialSwap ? "with delay" : "without delay")}");
            return true;
        }
        catch (Exception ex)
        {
            LogPluginError($"Error during weapon swap: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> CastSoulOffering(IInputController inputController)
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

        await MoveCursorSmoothly(targetPos, inputController);

        // Cast Soul Offering
        try
        {
            var key = Settings.SoulOfferingKey.Value;
            LogPluginMessage($"Casting Soul Offering with key: {key}");

            await inputController.KeyDown(key);
            await inputController.KeyUp(key);
            
            // Wait 1000ms after casting as specified
            await Task.Delay(1000);
        }
        catch (Exception ex)
        {
            LogPluginError($"Error during Soul Offering cast: {ex.Message}");
            return false;
        }

        _castTimer.Restart();
        LogPluginMessage("Soul Offering cast completed");
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

        if (GameController.IngameState.IngameUi.FullscreenPanels.Any(p => p.IsVisible))
        {
            state = "Fullscreen panel is open";
            return false;
        }

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

        if (AnyHostileMobsInRange(Settings.SafeRange.Value))
        {
            state = "Hostile mobs within 60 units - pausing for safety";
            return false;
        }

        if (GameController.Player.TryGetComponent<Life>(out var lifeComp))
        {
            if (lifeComp.CurHP <= 0)
            {
                state = "Player is dead";
                return false;
            }
        }

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
                LogPluginMessage($"No Infusion buff detected, starting sequence. Current weapon set: {_activeWeaponSetIndex}");
                _isActive = true;
                _currentState = SkillState.MovingCursor;
                return;
            }
            return;
        }

        switch (_currentState)
        {
            case SkillState.MovingCursor:
                var tryGetInputController = GameController.PluginBridge.GetMethod<Func<string, IInputController>>("InputHumanizer.TryGetInputController");
                if (tryGetInputController == null)
                {
                    LogError("InputHumanizer method not registered.");
                    _currentState = SkillState.Idle;
                    break;
                }

                if ((_inputController = tryGetInputController(this.Name)) != null)
                {
                    using (_inputController)
                    {
                        // If we need to swap weapons first
                        if (_activeWeaponSetIndex == 0)
                        {
                            await SwapWeapon(_inputController, true); // Initial swap with delay
                        }

                        var success = await CastSoulOffering(_inputController);
                        if (success)
                        {
                            // If we started in weapon set 0, we need to swap back
                            if (_activeWeaponSetIndex == 0)
                            {
                                await SwapWeapon(_inputController, false); // Return swap without delay
                            }
                            
                            _currentState = SkillState.WaitingForCastCheck;
                            LogPluginMessage("Cast sequence complete - Checking result");
                        }
                        else
                        {
                            _currentState = SkillState.RetryingCast;
                            LogPluginMessage("Cast failed - Will retry");
                        }
                    }
                }
                break;

            case SkillState.WaitingForCastCheck:
                if (_castTimer.ElapsedMilliseconds >= Settings.CastDelay.Value)
                {
                    bool buffFound = false;
                    for (int i = 0; i < 3 && !buffFound; i++)
                    {
                        UpdatePlayerState();
                        if (_hasInfusionBuff)
                        {
                            buffFound = true;
                            break;
                        }
                        await Task.Delay(20);
                    }

                    if (buffFound)
                    {
                        _currentState = SkillState.Idle;
                        _isActive = false;
                        LogPluginMessage("Buff acquired - Returning to idle");
                    }
                    else
                    {
                        LogPluginMessage("No buff detected after multiple checks - Retrying cast");
                        _currentState = SkillState.RetryingCast;
                    }
                }
                break;

            case SkillState.RetryingCast:
                _currentState = SkillState.MovingCursor;
                break;

            case SkillState.SwappingBack:
                _currentState = SkillState.Idle;
                _isActive = false;
                _targetSkeleton = null;
                LogPluginMessage("Sequence complete");
                break;
        }
    }
}
