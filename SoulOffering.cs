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
using System.Threading;
using RectangleF = ExileCore2.Shared.RectangleF;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;

namespace SoulOffering;

public class SoulOffering : BaseSettingsPlugin<SoulOfferingSettings>
{
    private bool _isActive = false;
    private const string SkeletonPath = "Metadata/Monsters/Skeletons/PlayerSummoned/SkeletonClericPlayerSummoned_";
    private readonly Stopwatch _weaponSwapTimer = Stopwatch.StartNew();
    private readonly Stopwatch _castTimer = Stopwatch.StartNew();
    private int _activeWeaponSetIndex;
    private bool _hasInfusionBuff;
    private Vector2 _savedCursorPosition;

    private bool IsInSafeZone()
    {
        var area = GameController.Area.CurrentArea;
        return area?.IsHideout == true || area?.IsTown == true;
    }

    // State machine
    private enum SkillState
    {
        Idle,
        WaitingForWeaponSwap,
        WaitingForCastCheck,
        RetryingCast,
        SwappingBack
    };
    public override void AreaChange(AreaInstance area)
    {
        base.AreaChange(area);

        // Register the IsActive method during AreaChange
        GameController.PluginBridge.SaveMethod("SoulOffering.IsActive", () => _isActive);
        LogMessage("SoulOffering.IsActive registered in PluginBridge (via AreaChange)");
    }

    private SkillState _currentState = SkillState.Idle;

    public override bool Initialise()
    {
        // Register input keys
        Settings.WeaponSwapKey.OnValueChanged += () => Input.RegisterKey(Settings.WeaponSwapKey);
        Settings.SoulOfferingKey.OnValueChanged += () => Input.RegisterKey(Settings.SoulOfferingKey);
        Input.RegisterKey(Settings.WeaponSwapKey);
        Input.RegisterKey(Settings.SoulOfferingKey);

        // Log initialization success
        LogMessage("Soul Offering initialized");

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
            _hasInfusionBuff = buffComp.HasBuff("infusion");
        }
    }

    private bool IsCursorOverEntity(Entity entity)
    {
        if (entity == null) return false;
        var hover = GameController.Game.IngameState.UIHoverElement;
        var hoverEntity = hover?.Entity;
        return hoverEntity != null && hoverEntity.Address == entity.Address;
    }

    private bool HasAliveSkeletons()
    {
        var aliveCount = GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster]
            .Count(x => x.Path.Contains(SkeletonPath) && x.IsAlive);

        if (Settings.DebugMode)
            LogMessage($"Found {aliveCount} alive skeletons");

        return aliveCount > 0;
    }

    private Vector2 GetClickPositionFor(Entity entity)
    {
        var entityPos = entity.GetComponent<Render>().Pos;
        var camera = GameController.Game.IngameState.Camera;
        var window = GameController.Window.GetWindowRectangleTimeCache;
        var screenPos = camera.WorldToScreen(entityPos);
        return window.TopLeft + screenPos;
    }

    private void SaveCursorPosition()
    {
        _savedCursorPosition = Input.ForceMousePosition;
        if (Settings.DebugMode)
            LogMessage($"Saved cursor position: X={_savedCursorPosition.X}, Y={_savedCursorPosition.Y}");
    }

    private void RestoreCursorPosition()
    {
        Input.SetCursorPos(_savedCursorPosition);
        if (Settings.DebugMode)
            LogMessage($"Restored cursor position: X={_savedCursorPosition.X}, Y={_savedCursorPosition.Y}");
    }

    private void SwapWeapon()
    {
        Input.KeyDown(Settings.WeaponSwapKey.Value);
        Input.KeyUp(Settings.WeaponSwapKey.Value);
        _weaponSwapTimer.Restart();
        LogMessage("Weapon swap performed");
    }

    private bool CastSoulOffering()
    {
        var skeletons = GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster]
            .Where(x => x.Path.Contains(SkeletonPath) && x.IsAlive)
            .ToList();

        if (!skeletons.Any()) return false;

        SaveCursorPosition();
        var skeleton = skeletons.First();
        var targetPos = GetClickPositionFor(skeleton);
        Input.SetCursorPos(targetPos);

        if (!IsCursorOverEntity(skeleton))
        {
            Thread.Sleep(50);
            LogMessage("Failed to target skeleton");
        }

        Input.KeyDown(Settings.SoulOfferingKey.Value);
        Thread.Sleep(10);
        Input.KeyUp(Settings.SoulOfferingKey.Value);
        Thread.Sleep(1000); // Wait 1000ms after pressing Q

        _isActive = false; // Mark inactive 1 second after casting
        _castTimer.Restart();
        LogMessage("Soul Offering cast at skeleton position");
        RestoreCursorPosition();
        return true;
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

        if (Settings.DisableInSafeZones.Value && IsInSafeZone())
        {
            state = $"Player is in a safe zone ({GameController.Area.CurrentArea.Name})";
            return false;
        }

        state = "Ready";
        return true;
    }

    public override void Tick()
    {
        if (!ShouldExecute(out string state))
        {
            if (Settings.DebugMode)
                LogMessage($"Plugin paused: {state}");
            _currentState = SkillState.Idle;
            _isActive = false; // Ensure it's inactive when disabled
            return;
        }

        UpdatePlayerState();

        if (_currentState == SkillState.Idle && !_hasInfusionBuff)
        {
            LogMessage($"No Infusion buff detected, starting sequence. Current weapon set: {_activeWeaponSetIndex}");
            _isActive = true; // Mark active at the start of the sequence

            if (_activeWeaponSetIndex == 0)
            {
                SwapWeapon();
                _currentState = SkillState.WaitingForWeaponSwap;
                LogMessage("Swapping weapons");
            }
            else
            {
                if (CastSoulOffering())
                {
                    _currentState = SkillState.WaitingForCastCheck;
                    LogMessage("Casting Soul Offering");
                }
            }
            return;
        }

        switch (_currentState)
        {
            case SkillState.WaitingForWeaponSwap:
                if (_weaponSwapTimer.ElapsedMilliseconds >= Settings.WeaponSwapDelay.Value)
                {
                    if (CastSoulOffering())
                    {
                        _currentState = SkillState.WaitingForCastCheck;
                        LogMessage("Weapon swap complete - Casting");
                    }
                }
                break;

            case SkillState.WaitingForCastCheck:
                if (_castTimer.ElapsedMilliseconds >= Settings.CastDelay.Value)
                {
                    UpdatePlayerState();
                    if (_hasInfusionBuff)
                    {
                        if (_activeWeaponSetIndex == 1)
                        {
                            SwapWeapon();
                            _currentState = SkillState.SwappingBack;
                            LogMessage("Buff acquired - Swapping back");
                        }
                        else
                        {
                            _currentState = SkillState.Idle;
                            _isActive = false; // Mark inactive when done
                            LogMessage("Buff acquired - Returning to idle");
                        }
                    }
                    else
                    {
                        _currentState = SkillState.RetryingCast;
                        LogMessage("No buff detected - Retrying cast");
                    }
                }
                break;

            case SkillState.RetryingCast:
                if (CastSoulOffering())
                {
                    _currentState = SkillState.WaitingForCastCheck;
                    LogMessage("Retrying Soul Offering cast");
                }
                break;

            case SkillState.SwappingBack:
                if (_weaponSwapTimer.ElapsedMilliseconds >= Settings.WeaponSwapDelay.Value)
                {
                    _currentState = SkillState.Idle;
                    _isActive = false; // Mark inactive when finished
                    LogMessage("Sequence complete");
                }
                break;
        }
    }
}