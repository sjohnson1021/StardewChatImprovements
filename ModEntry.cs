using GenericModConfigMenu;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace ChatImprovements;

/// <summary>
/// Main entry point for the Chat Improvements mod.
/// Handles Harmony patching, configuration, and GMCM integration.
/// </summary>
public sealed class ModEntry : Mod
{
    #region Fields

    private Harmony? _harmony;

    #endregion

    #region Properties

    /// <summary>
    /// Singleton instance of the mod for access from Harmony patches.
    /// </summary>
    public static ModEntry? Instance { get; private set; }

    /// <summary>
    /// Current mod configuration loaded from config.json.
    /// </summary>
    public ModConfig Config { get; private set; } = new();

    #endregion

    #region Lifecycle

    /// <summary>
    /// Mod initialization entry point.
    /// Loads configuration, applies Harmony patches, and registers event handlers.
    /// </summary>
    /// <param name="helper">SMAPI helper for accessing mod APIs and events.</param>
    public override void Entry(IModHelper helper)
    {
        Instance = this;
        Config = helper.ReadConfig<ModConfig>();

        ApplyHarmonyPatches();

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
    }

    /// <summary>
    /// Applies all Harmony patches defined in this mod.
    /// Patches are applied to ChatBox, ChatTextBox, and ChatMessage classes.
    /// </summary>
    private void ApplyHarmonyPatches()
    {
        _harmony = new Harmony(ModManifest.UniqueID);

        try
        {
            _harmony.PatchAll();
            Monitor.Log("Successfully applied Harmony patches.", LogLevel.Trace);
        }
        catch (Exception ex)
        {
            Monitor.Log($"CRITICAL: Failed to apply Harmony patches. Mod will not function.", LogLevel.Error);
            Monitor.Log($"Exception: {ex.GetType().Name}", LogLevel.Error);
            Monitor.Log($"Message: {ex.Message}", LogLevel.Error);
            Monitor.Log($"Stack Trace: {ex.StackTrace}", LogLevel.Error);
        }
    }

    #endregion

    #region Event Handlers

    /// <summary>
    /// Triggered when the game is fully launched.
    /// Registers configuration options with Generic Mod Config Menu if available.
    /// </summary>
    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        IGenericModConfigMenuApi? configApi =
            Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");

        if (configApi is null)
        {
            Monitor.Log("Generic Mod Config Menu not found. In-game configuration will not be available.",
                LogLevel.Debug);
            return;
        }

        RegisterConfigMenu(configApi);
    }

    #endregion

    #region GMCM Integration

    /// <summary>
    /// Registers all configuration options with Generic Mod Config Menu.
    /// </summary>
    private void RegisterConfigMenu(IGenericModConfigMenuApi api)
    {
        api.Register(
            manifest: ModManifest,
            reset: () => Config = new ModConfig(),
            save: () => Helper.WriteConfig(Config)
        );

        RegisterGeneralSettings(api);
        RegisterFeatureToggles(api);
        RegisterInputSettings(api);
        RegisterKeybinds(api);

        Monitor.Log("Registered configuration options with Generic Mod Config Menu.", LogLevel.Trace);
    }

    /// <summary>
    /// Registers general settings (message length, history size) with GMCM.
    /// </summary>
    private void RegisterGeneralSettings(IGenericModConfigMenuApi api)
    {
        var t = Helper.Translation;

        api.AddNumberOption(
            manifest: ModManifest,
            name: () => t.Get("config.maxMessageLength.name"),
            tooltip: () => t.Get("config.maxMessageLength.tooltip"),
            getValue: () => Config.MaxMessageLength,
            setValue: value => Config.MaxMessageLength = value,
            min: 100,
            max: 1000
        );

        api.AddNumberOption(
            manifest: ModManifest,
            name: () => t.Get("config.maxChatHistory.name"),
            tooltip: () => t.Get("config.maxChatHistory.tooltip"),
            getValue: () => Config.MaxChatHistory,
            setValue: value => Config.MaxChatHistory = value,
            min: 10,
            max: 500
        );
    }

    /// <summary>
    /// Registers feature toggle options (scrolling, cursor control, URL clicking) with GMCM.
    /// </summary>
    private void RegisterFeatureToggles(IGenericModConfigMenuApi api)
    {
        var t = Helper.Translation;

        api.AddBoolOption(
            manifest: ModManifest,
            name: () => t.Get("config.enableHorizontalScrolling.name"),
            tooltip: () => t.Get("config.enableHorizontalScrolling.tooltip"),
            getValue: () => Config.EnableHorizontalScrolling,
            setValue: value => Config.EnableHorizontalScrolling = value
        );

        api.AddBoolOption(
            manifest: ModManifest,
            name: () => t.Get("config.enableCursorControl.name"),
            tooltip: () => t.Get("config.enableCursorControl.tooltip"),
            getValue: () => Config.EnableCursorControl,
            setValue: value => Config.EnableCursorControl = value
        );
        api.AddBoolOption(
            manifest: ModManifest,
            name: () => t.Get("config.enableMessageColorButton.name"),
            tooltip: () => t.Get("config.enableMessageColorButton.tooltip"),
            getValue: () => Config.EnableMessageColorButton,
            setValue: value => Config.EnableMessageColorButton = value
        );
        api.AddBoolOption(
            manifest: ModManifest,
            name: () => t.Get("config.allowUrlClickWhenChatClosed.name"),
            tooltip: () => t.Get("config.allowUrlClickWhenChatClosed.tooltip"),
            getValue: () => Config.AllowUrlClickWhenChatClosed,
            setValue: value => Config.AllowUrlClickWhenChatClosed = value
        );
    }

    /// <summary>
    /// Registers keyboard input settings (repeat delays) with GMCM.
    /// </summary>
    private void RegisterInputSettings(IGenericModConfigMenuApi api)
    {
        var t = Helper.Translation;

        api.AddNumberOption(
            manifest: ModManifest,
            name: () => t.Get("config.keyRepeatInitialDelay.name"),
            tooltip: () => t.Get("config.keyRepeatInitialDelay.tooltip"),
            getValue: () => Config.KeyRepeatInitialDelay,
            setValue: value => Config.KeyRepeatInitialDelay = value,
            min: 0.25f,
            max: 3.0f,
            interval: 0.05f
        );

        api.AddNumberOption(
            manifest: ModManifest,
            name: () => t.Get("config.keyRepeatDelay.name"),
            tooltip: () => t.Get("config.keyRepeatDelay.tooltip"),
            getValue: () => Config.KeyRepeatDelay,
            setValue: value => Config.KeyRepeatDelay = value,
            min: 0.01f,
            max: 0.5f,
            interval: 0.01f
        );
    }

    /// <summary>
    /// Registers all keybind options (copy, paste, undo, etc.) with GMCM.
    /// </summary>
    private void RegisterKeybinds(IGenericModConfigMenuApi api)
    {
        var t = Helper.Translation;

        api.AddKeybindList(
            manifest: ModManifest,
            getValue: () => Config.SelectAllKeybind,
            setValue: value => Config.SelectAllKeybind = value,
            name: () => t.Get("config.selectAllKeybind.name"),
            tooltip: () => t.Get("config.selectAllKeybind.tooltip")
        );

        api.AddKeybindList(
            manifest: ModManifest,
            getValue: () => Config.CopyKeybind,
            setValue: value => Config.CopyKeybind = value,
            name: () => t.Get("config.copyKeybind.name"),
            tooltip: () => t.Get("config.copyKeybind.tooltip")
        );

        api.AddKeybindList(
            manifest: ModManifest,
            getValue: () => Config.CutKeybind,
            setValue: value => Config.CutKeybind = value,
            name: () => t.Get("config.cutKeybind.name"),
            tooltip: () => t.Get("config.cutKeybind.tooltip")
        );

        api.AddKeybindList(
            manifest: ModManifest,
            getValue: () => Config.PasteKeybind,
            setValue: value => Config.PasteKeybind = value,
            name: () => t.Get("config.pasteKeybind.name"),
            tooltip: () => t.Get("config.pasteKeybind.tooltip")
        );

        api.AddKeybindList(
            manifest: ModManifest,
            getValue: () => Config.UndoKeybind,
            setValue: value => Config.UndoKeybind = value,
            name: () => t.Get("config.undoKeybind.name"),
            tooltip: () => t.Get("config.undoKeybind.tooltip")
        );

        api.AddKeybindList(
            manifest: ModManifest,
            getValue: () => Config.RedoKeybind,
            setValue: value => Config.RedoKeybind = value,
            name: () => t.Get("config.redoKeybind.name"),
            tooltip: () => t.Get("config.redoKeybind.tooltip")
        );
    }

    #endregion
}