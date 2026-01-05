using GenericModConfigMenu;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace ChatImprovements;

public sealed class ModEntry : Mod
{
    private Harmony? harmony;
    public static ModEntry? Instance { get; private set; } // Make accessible
    public ModConfig Config { get; private set; } = new(); // Make public + initialize

    public override void Entry(IModHelper helper)
    {
        Instance = this;
        Config = helper.ReadConfig<ModConfig>();
        harmony = new Harmony(ModManifest.UniqueID);
        harmony.PatchAll();
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        // Get GMCM API correctly
        IGenericModConfigMenuApi? api =
            Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (api is null) return;

        // Use the api directly (not configMenu)
        api.Register(
            ModManifest,
            () => Config = new ModConfig(),
            () => Helper.WriteConfig(Config)
        );

        api.AddNumberOption(
            ModManifest,
            name: () => "Max Message Length",
            tooltip: () => "The maximum number of characters allowed in a chat message.",
            getValue: () => Config.MaxMessageLength,
            setValue: value => Config.MaxMessageLength = value,
            min: 100,
            max: 5000
        );

        api.AddBoolOption(
            ModManifest,
            name: () => "Enable Horizontal Scrolling",
            tooltip: () => "Whether to enable horizontal scrolling when typing long messages.",
            getValue: () => Config.EnableHorizontalScrolling,
            setValue: value => Config.EnableHorizontalScrolling = value
        );

        api.AddBoolOption(
            ModManifest,
            name: () => "Enable Cursor Control",
            tooltip: () => "Whether to enable cursor control (arrow keys for navigation).",
            getValue: () => Config.EnableCursorControl,
            setValue: value => Config.EnableCursorControl = value
        );

        api.AddBoolOption(
            ModManifest,
            name: () => "Allow URL Clicks When Chat Closed",
            tooltip: () =>
                "If enabled, you can click URLs in messages even when the chat box is not open. Disable to prevent accidental clicks.",
            getValue: () => Config.AllowUrlClickWhenChatClosed,
            setValue: value => Config.AllowUrlClickWhenChatClosed = value
        );

        api.AddNumberOption(
            ModManifest,
            name: () => "Key Repeat Initial Delay",
            tooltip: () => "The initial delay in seconds before arrow key repeat starts when holding the key.",
            getValue: () => Config.KeyRepeatInitialDelay,
            setValue: value => Config.KeyRepeatInitialDelay = value,
            min: 0.25f,
            max: 3.0f,
            interval: 0.05f
        );

        api.AddNumberOption(
            ModManifest,
            name: () => "Key Repeat Delay",
            tooltip: () => "The delay in seconds between repeated cursor movements when holding arrow keys.",
            getValue: () => Config.KeyRepeatDelay,
            setValue: value => Config.KeyRepeatDelay = value,
            min: 0.01f,
            max: 0.5f,
            interval: 0.01f
        );

        api.AddKeybindList(
            ModManifest,
            () => Config.CopyKeybind,
            value => Config.CopyKeybind = value,
            () => "Copy Keybind",
            () => "Keybind to copy selected text to clipboard"
        );

        api.AddKeybindList(
            ModManifest,
            () => Config.CutKeybind,
            value => Config.CutKeybind = value,
            () => "Cut Keybind",
            () => "Keybind to cut selected text to clipboard"
        );

        api.AddKeybindList(
            ModManifest,
            () => Config.PasteKeybind,
            value => Config.PasteKeybind = value,
            () => "Paste Keybind",
            () => "Keybind to paste text from clipboard"
        );
    }
}