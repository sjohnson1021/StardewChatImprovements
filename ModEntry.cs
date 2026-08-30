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

    /// <summary>Unique ID of the Item Chat Link mod, which also draws chat messages itself.</summary>
    private const string ItemChatLinkId = "juhyu.ItemChatLink";

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

    /// <summary>
    /// Whether Item Chat Link is installed.
    /// </summary>
    /// <remarks>
    ///     It draws messages containing an item link itself, from its own prefix on
    ///     <c>ChatMessage.draw</c>. Two prefixes cannot both take over one method, so this mod
    ///     steps aside for those messages rather than silencing the other mod's whole feature.
    /// </remarks>
    public static bool ItemChatLinkLoaded { get; private set; }

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
        // Checked here rather than in Entry: SMAPI loads mods in order, and this one sorts
        // ahead of Item Chat Link, so during Entry the registry does not know about it yet.
        // Getting this wrong made every compatibility path below silently dead code.
        ItemChatLinkLoaded = Helper.ModRegistry.IsLoaded(ItemChatLinkId);
        if (ItemChatLinkLoaded)
            Monitor.Log($"Detected {ItemChatLinkId}; item-link messages will be left for it to draw.",
                LogLevel.Trace);

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

    #region Multiplayer

    /// <summary>
    /// Whether any connected player would render a longer-than-vanilla message incorrectly.
    /// </summary>
    /// <remarks>
    ///     A client without this mod reserves a message's height from one text wrap and then
    ///     draws it with another, so a message long enough to wrap overlaps its neighbours on
    ///     their screen. Nothing can be patched there, so the sender has to split the message
    ///     instead -- but only when someone is actually going to be affected, since splitting
    ///     costs everyone else the single-message view.
    /// </remarks>
    public bool AnyConnectedPlayerLacksThisMod()
    {
        foreach (IMultiplayerPeer peer in Helper.Multiplayer.GetConnectedPlayers())
        {
            if (!peer.HasSmapi)
                return true;

            if (peer.GetMod(ModManifest.UniqueID) is null)
                return true;
        }

        return false;
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

        RegisterMessageSettings(api);
        RegisterEditingSettings(api);
        RegisterAppearanceSettings(api);
        RegisterLinkSettings(api);
        RegisterKeybinds(api);

        Monitor.Log("Registered configuration options with Generic Mod Config Menu.", LogLevel.Trace);
    }

    /// <summary>Message length and how much scrollback is kept.</summary>
    private void RegisterMessageSettings(IGenericModConfigMenuApi api)
    {
        var t = Helper.Translation;

        api.AddSectionTitle(ModManifest,
            () => t.Get("config.section.messages.name"),
            () => t.Get("config.section.messages.desc"));

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

        api.AddTextOption(
            manifest: ModManifest,
            name: () => t.Get("config.splitLongMessages.name"),
            tooltip: () => t.Get("config.splitLongMessages.tooltip"),
            getValue: () => Config.SplitLongMessages.ToString(),
            setValue: value => Config.SplitLongMessages =
                Enum.TryParse(value, out VanillaSplitMode parsed) ? parsed : VanillaSplitMode.Auto,
            allowedValues: new[]
            {
                nameof(VanillaSplitMode.Auto),
                nameof(VanillaSplitMode.Always),
                nameof(VanillaSplitMode.Never)
            },
            formatAllowedValue: value => value switch
            {
                nameof(VanillaSplitMode.Always) => t.Get("config.splitLongMessages.always"),
                nameof(VanillaSplitMode.Never) => t.Get("config.splitLongMessages.never"),
                _ => t.Get("config.splitLongMessages.auto")
            }
        );
    }

    /// <summary>Cursor movement, selection and key repeat.</summary>
    private void RegisterEditingSettings(IGenericModConfigMenuApi api)
    {
        var t = Helper.Translation;

        api.AddSectionTitle(ModManifest,
            () => t.Get("config.section.editing.name"),
            () => t.Get("config.section.editing.desc"));

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

    /// <summary>What the chat box displays alongside the text.</summary>
    private void RegisterAppearanceSettings(IGenericModConfigMenuApi api)
    {
        var t = Helper.Translation;

        api.AddSectionTitle(ModManifest,
            () => t.Get("config.section.appearance.name"),
            () => t.Get("config.section.appearance.desc"));

        api.AddBoolOption(
            manifest: ModManifest,
            name: () => t.Get("config.enableMessageColorButton.name"),
            tooltip: () => t.Get("config.enableMessageColorButton.tooltip"),
            getValue: () => Config.EnableMessageColorButton,
            setValue: value => Config.EnableMessageColorButton = value
        );
    }

    /// <summary>How links in chat behave.</summary>
    private void RegisterLinkSettings(IGenericModConfigMenuApi api)
    {
        var t = Helper.Translation;

        api.AddSectionTitle(ModManifest,
            () => t.Get("config.section.links.name"),
            () => t.Get("config.section.links.desc"));

        api.AddBoolOption(
            manifest: ModManifest,
            name: () => t.Get("config.allowUrlClickWhenChatClosed.name"),
            tooltip: () => t.Get("config.allowUrlClickWhenChatClosed.tooltip"),
            getValue: () => Config.AllowUrlClickWhenChatClosed,
            setValue: value => Config.AllowUrlClickWhenChatClosed = value
        );

        api.AddTextOption(
            manifest: ModManifest,
            name: () => t.Get("config.linkClickBehavior.name"),
            tooltip: () => t.Get("config.linkClickBehavior.tooltip"),
            getValue: () => Config.LinkClickBehavior.ToString(),
            setValue: value => Config.LinkClickBehavior =
                Enum.TryParse(value, out LinkClickAction parsed) ? parsed : LinkClickAction.Copy,
            allowedValues: new[] { nameof(LinkClickAction.Copy), nameof(LinkClickAction.Open) },
            formatAllowedValue: value => value == nameof(LinkClickAction.Open)
                ? t.Get("config.linkClickBehavior.open")
                : t.Get("config.linkClickBehavior.copy")
        );
    }

    /// <summary>Shortcuts available while the chat box is open.</summary>
    private void RegisterKeybinds(IGenericModConfigMenuApi api)
    {
        var t = Helper.Translation;

        api.AddSectionTitle(ModManifest,
            () => t.Get("config.section.keybinds.name"),
            () => t.Get("config.section.keybinds.desc"));

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