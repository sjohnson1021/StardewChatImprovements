using GenericModConfigMenu;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace ChatImprovements;

public sealed class ModEntry : Mod
{
    private Harmony? harmony;
    public static ModEntry? Instance { get; private set; }
    public ModConfig Config { get; private set; } = new();

    public override void Entry(IModHelper helper)
    {
        Instance = this;
        Config = helper.ReadConfig<ModConfig>();
        harmony = new Harmony(ModManifest.UniqueID);
        try
        {
            harmony.PatchAll();
            Monitor.Log("Applied Harmony patches.", LogLevel.Trace);
        }
        catch (Exception ex)
        {
            Monitor.Log($"Failed to apply Harmony patches: {ex}", LogLevel.Error);
        }

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        IGenericModConfigMenuApi? api =
            Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (api is null) return;

        var t = Helper.Translation;

        api.Register(
            ModManifest,
            () => Config = new ModConfig(),
            () => Helper.WriteConfig(Config)
        );

        api.AddNumberOption(
            ModManifest,
            name: () => t.Get("config.maxMessageLength.name"),
            tooltip: () => t.Get("config.maxMessageLength.tooltip"),
            getValue: () => Config.MaxMessageLength,
            setValue: value => Config.MaxMessageLength = value,
            min: 100,
            max: 1000
        );

        api.AddNumberOption(
            ModManifest,
            name: () => t.Get("config.maxChatHistory.name"),
            tooltip: () => t.Get("config.maxChatHistory.tooltip"),
            getValue: () => Config.MaxChatHistory,
            setValue: value => Config.MaxChatHistory = value,
            min: 10,
            max: 500
        );

        api.AddBoolOption(
            ModManifest,
            name: () => t.Get("config.enableHorizontalScrolling.name"),
            tooltip: () => t.Get("config.enableHorizontalScrolling.tooltip"),
            getValue: () => Config.EnableHorizontalScrolling,
            setValue: value => Config.EnableHorizontalScrolling = value
        );

        api.AddBoolOption(
            ModManifest,
            name: () => t.Get("config.enableCursorControl.name"),
            tooltip: () => t.Get("config.enableCursorControl.tooltip"),
            getValue: () => Config.EnableCursorControl,
            setValue: value => Config.EnableCursorControl = value
        );

        api.AddBoolOption(
            ModManifest,
            name: () => t.Get("config.allowUrlClickWhenChatClosed.name"),
            tooltip: () => t.Get("config.allowUrlClickWhenChatClosed.tooltip"),
            getValue: () => Config.AllowUrlClickWhenChatClosed,
            setValue: value => Config.AllowUrlClickWhenChatClosed = value
        );

        api.AddNumberOption(
            ModManifest,
            name: () => t.Get("config.keyRepeatInitialDelay.name"),
            tooltip: () => t.Get("config.keyRepeatInitialDelay.tooltip"),
            getValue: () => Config.KeyRepeatInitialDelay,
            setValue: value => Config.KeyRepeatInitialDelay = value,
            min: 0.25f,
            max: 3.0f,
            interval: 0.05f
        );

        api.AddNumberOption(
            ModManifest,
            name: () => t.Get("config.keyRepeatDelay.name"),
            tooltip: () => t.Get("config.keyRepeatDelay.tooltip"),
            getValue: () => Config.KeyRepeatDelay,
            setValue: value => Config.KeyRepeatDelay = value,
            min: 0.01f,
            max: 0.5f,
            interval: 0.01f
        );

        api.AddKeybindList(
            ModManifest,
            () => Config.SelectAllKeybind,
            value => Config.SelectAllKeybind = value,
            () => t.Get("config.selectAllKeybind.name"),
            () => t.Get("config.selectAllKeybind.tooltip")
        );

        api.AddKeybindList(
            ModManifest,
            () => Config.CopyKeybind,
            value => Config.CopyKeybind = value,
            () => t.Get("config.copyKeybind.name"),
            () => t.Get("config.copyKeybind.tooltip")
        );

        api.AddKeybindList(
            ModManifest,
            () => Config.CutKeybind,
            value => Config.CutKeybind = value,
            () => t.Get("config.cutKeybind.name"),
            () => t.Get("config.cutKeybind.tooltip")
        );

        api.AddKeybindList(
            ModManifest,
            () => Config.PasteKeybind,
            value => Config.PasteKeybind = value,
            () => t.Get("config.pasteKeybind.name"),
            () => t.Get("config.pasteKeybind.tooltip")
        );

        api.AddKeybindList(
            ModManifest,
            () => Config.UndoKeybind,
            value => Config.UndoKeybind = value,
            () => t.Get("config.undoKeybind.name"),
            () => t.Get("config.undoKeybind.tooltip")
        );

        api.AddKeybindList(
            ModManifest,
            () => Config.RedoKeybind,
            value => Config.RedoKeybind = value,
            () => t.Get("config.redoKeybind.name"),
            () => t.Get("config.redoKeybind.tooltip")
        );
    }
}