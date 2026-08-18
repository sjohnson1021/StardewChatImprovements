using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using StardewModdingAPI;

namespace ChatImprovements;

/// <summary>
/// Helper for cross-platform clipboard operations using SDL2 and native utilities.
/// </summary>
internal static class ClipboardHelper
{
    #region SDL2 Imports

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_GetClipboardText();

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_free(IntPtr mem);

    [DllImport("SDL2", EntryPoint = "SDL_SetClipboardText", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_SetClipboardText_Internal(IntPtr text);

    #endregion

    /// <summary>
    /// Gets text from the system clipboard.
    /// Uses SDL2 which handles Windows, macOS, and X11/Wayland (usually).
    /// </summary>
    public static string GetText()
    {
        try
        {
            IntPtr ptr = SDL_GetClipboardText();
            if (ptr == IntPtr.Zero)
                return "";

            string text = Marshal.PtrToStringUTF8(ptr) ?? "";
            SDL_free(ptr);
            return text;
        }
        catch (Exception ex)
        {
            ModEntry.Instance?.Monitor.Log($"Clipboard read error: {ex.Message}", LogLevel.Error);
            return "";
        }
    }

    /// <summary>
    /// Sets text to the system clipboard.
    /// Includes special handling for Wayland (Linux).
    /// </summary>
    public static void SetText(string text)
    {
        try
        {
            if (TrySetClipboardLinux(text))
                return;

            // Fallback / Standard SDL2 for other platforms
            SetClipboardSdl(text);
        }
        catch (Exception ex)
        {
            ModEntry.Instance?.Monitor.Log($"Clipboard write error: {ex.Message}", LogLevel.Error);
        }
    }

    private static bool TrySetClipboardLinux(string text)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        try
        {
            // Attempt to use wl-copy for Wayland support
            Process? p = Process.Start(new ProcessStartInfo
            {
                FileName = "wl-copy",
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (p != null)
            {
                p.StandardInput.Write(text);
                p.StandardInput.Close();
                p.WaitForExit();
                return true;
            }
        }
        catch
        {
            // wl-copy missing or failed, fall back to SDL2
        }

        return false;
    }

    private static void SetClipboardSdl(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text + "\0");
        GCHandle handle = GCHandle.Alloc(utf8, GCHandleType.Pinned);
        try
        {
            _ = SDL_SetClipboardText_Internal(handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }
}
