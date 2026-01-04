using System;
using System.Xml.Linq;
using GenericModConfigMenu;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using HarmonyLib;
namespace ChatImprovements
{
    public sealed class ModEntry : Mod
    {
        public static ModEntry? Instance { get; private set; } // Make accessible
        public ModConfig Config { get; private set; } = new(); // Make public + initialize
        private Harmony? harmony;

        public override void Entry(IModHelper helper)
        {
            Instance = this;
            Config = helper.ReadConfig<ModConfig>();
            harmony = new Harmony(this.ModManifest.UniqueID);
            harmony.PatchAll();
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            // Get GMCM API correctly
            var api = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (api is null) return;

            // Use the api directly (not configMenu)
            api.Register(
                mod: this.ModManifest,
                reset: () => Config = new ModConfig(),
                save: () => Helper.WriteConfig(Config)
            );

            api.AddNumberOption(
                mod: this.ModManifest,
                name: () => "Max Message Length",
                tooltip: () => "The maximum number of characters allowed in a chat message.",
                getValue: () => Config.MaxMessageLength,
                setValue: value => Config.MaxMessageLength = value,
                min: 100,
                max: 5000
            );

            api.AddBoolOption(
                mod: this.ModManifest,
                name: () => "Enable Horizontal Scrolling",
                tooltip: () => "Whether to enable horizontal scrolling when typing long messages.",
                getValue: () => Config.EnableHorizontalScrolling,
                setValue: value => Config.EnableHorizontalScrolling = value
            );

            api.AddBoolOption(
                mod: this.ModManifest,
                name: () => "Enable Cursor Control",
                tooltip: () => "Whether to enable cursor control (arrow keys for navigation).",
                getValue: () => Config.EnableCursorControl,
                setValue: value => Config.EnableCursorControl = value
            );

            api.AddNumberOption(
                mod: this.ModManifest,
                name: () => "Key Repeat Initial Delay",
                tooltip: () => "The initial delay in seconds before arrow key repeat starts when holding the key.",
                getValue: () => Config.KeyRepeatInitialDelay,
                setValue: value => Config.KeyRepeatInitialDelay = value,
                min: 0.25f,
                max: 3.0f,
                interval: 0.05f
            );

            api.AddNumberOption(
                mod: this.ModManifest,
                name: () => "Key Repeat Delay",
                tooltip: () => "The delay in seconds between repeated cursor movements when holding arrow keys.",
                getValue: () => Config.KeyRepeatDelay,
                setValue: value => Config.KeyRepeatDelay = value,
                min: 0.01f,
                max: 0.5f,
                interval: 0.01f
            );

            api.AddKeybindList(
                mod: this.ModManifest,
                getValue: () => Config.CopyKeybind,
                setValue: value => Config.CopyKeybind = value,
                name: () => "Copy Keybind",
                tooltip: () => "Keybind to copy selected text to clipboard"
            );

            api.AddKeybindList(
                mod: this.ModManifest,
                getValue: () => Config.CutKeybind,
                setValue: value => Config.CutKeybind = value,
                name: () => "Cut Keybind",
                tooltip: () => "Keybind to cut selected text to clipboard"
            );

            api.AddKeybindList(
                mod: this.ModManifest,
                getValue: () => Config.PasteKeybind,
                setValue: value => Config.PasteKeybind = value,
                name: () => "Paste Keybind",
                tooltip: () => "Keybind to paste text from clipboard"
            );
        }
    }
}
