namespace pxdChatterGuard.Core;

/// <summary>
/// Keys are identified by their physical position, using PS/2 "set 1" scancodes: 0x1E is always the key right of Caps
/// Lock, whatever the layout says it types. Extended keys (right Ctrl, arrows, ...) are 0xE000 plus the scancode. Every
/// platform backend translates its native codes into these, so settings mean the same thing on every system.
/// <para>
/// Mouse buttons get codes of their own, far above any scancode (<see cref="MouseLeft"/> to <see cref="MouseForward"/>),
/// so they go through the same engine as keys: their own delays, the chatter monitor and the caught list.
/// </para>
/// </summary>
public static class KeyCatalog
{
    private static readonly HashSet<int> Modifiers = new() { 0x1D, 0xE01D, 0x2A, 0x36, 0x38, 0xE038, 0xE05B, 0xE05C };

    // Caps Lock, Num Lock (reported with or without the extended flag) and Scroll Lock.
    private static readonly HashSet<int> Locks = new() { 0x3A, 0x45, 0xE045, 0x46 };

    /// <summary>The primary mouse button (the left one unless the buttons are swapped in the system settings).</summary>
    public const int MouseLeft = 0x10001;

    public const int MouseRight = 0x10002;
    public const int MouseMiddle = 0x10003;

    /// <summary>The side button for "back" (X1, also called mouse 4).</summary>
    public const int MouseBack = 0x10004;

    /// <summary>The side button for "forward" (X2, also called mouse 5).</summary>
    public const int MouseForward = 0x10005;

    /// <summary>How many mouse buttons there are; <see cref="MouseLeft"/> + n is button n.</summary>
    public const int MouseButtonCount = 5;

    public static bool IsMouseButton(int key) => key is >= MouseLeft and <= MouseForward;

    /// <summary>Keys that switch something on and off when pressed.</summary>
    public static bool IsLockKey(int key) => Locks.Contains(key);

    /// <summary>Every key the app can set an exception for, in keyboard order.</summary>
    public static IReadOnlyList<(int Code, string Name)> All { get; } = Build();

    /// <summary>The mouse buttons that can be filtered (Windows), in the order they are listed.</summary>
    public static IReadOnlyList<(int Code, string Name)> MouseButtons { get; } = new (int, string)[]
    {
        (MouseLeft, "Left click"), (MouseRight, "Right click"), (MouseMiddle, "Middle click"),
        (MouseBack, "Mouse back"), (MouseForward, "Mouse forward"),
    };

    private static readonly Dictionary<int, string> Names = All.Concat(MouseButtons).ToDictionary(k => k.Code, k => k.Name);

    public static bool IsModifier(int key) => Modifiers.Contains(key);

    /// <summary>A readable name; unknown keys show their code so nothing is ever hidden.</summary>
    public static string NameOf(int key) => Names.TryGetValue(key, out var n) ? n : $"Key {key:X}";

    /// <summary>"button" for a mouse button, otherwise "key", for sentences about one.</summary>
    public static string NounFor(int key) => IsMouseButton(key) ? "button" : "key";

    private static List<(int, string)> Build()
    {
        var keys = new List<(int, string)>
        {
            (0x01, "Esc"),
            (0x29, "` (backtick)"),
        };

        for (var i = 0; i < 9; i++)
            keys.Add((0x02 + i, (i + 1).ToString()));
        keys.Add((0x0B, "0"));
        keys.Add((0x0C, "-"));
        keys.Add((0x0D, "="));
        keys.Add((0x0E, "Backspace"));
        keys.Add((0x0F, "Tab"));
        AddRow(keys, 0x10, "QWERTYUIOP");
        keys.Add((0x1A, "["));
        keys.Add((0x1B, "]"));
        keys.Add((0x2B, "\\"));
        keys.Add((0x3A, "Caps Lock"));
        AddRow(keys, 0x1E, "ASDFGHJKL");
        keys.Add((0x27, ";"));
        keys.Add((0x28, "'"));
        keys.Add((0x1C, "Enter"));
        AddRow(keys, 0x2C, "ZXCVBNM");
        keys.Add((0x33, ","));
        keys.Add((0x34, "."));
        keys.Add((0x35, "/"));
        keys.Add((0x39, "Space"));
        keys.Add((0x56, "ISO \\ (extra key)"));

        int[] f = { 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58 };
        for (var i = 0; i < f.Length; i++)
            keys.Add((f[i], $"F{i + 1}"));

        keys.AddRange(new (int, string)[]
        {
            (0xE048, "Up"), (0xE050, "Down"), (0xE04B, "Left"), (0xE04D, "Right"),
            (0xE052, "Insert"), (0xE053, "Delete"), (0xE047, "Home"), (0xE04F, "End"), (0xE049, "Page Up"), (0xE051, "Page Down"),
            (0xE037, "Print Screen"), (0x46, "Scroll Lock"),
            (0x45, "Num Lock"), (0xE035, "Numpad /"), (0x37, "Numpad *"), (0x4A, "Numpad -"), (0x4E, "Numpad +"), (0xE01C, "Numpad Enter"), (0x53, "Numpad ."),
            (0x52, "Numpad 0"), (0x4F, "Numpad 1"), (0x50, "Numpad 2"), (0x51, "Numpad 3"), (0x4B, "Numpad 4"), (0x4C, "Numpad 5"),
            (0x4D, "Numpad 6"), (0x47, "Numpad 7"), (0x48, "Numpad 8"), (0x49, "Numpad 9"),
            (0x2A, "Left Shift"), (0x36, "Right Shift"), (0x1D, "Left Ctrl"), (0xE01D, "Right Ctrl"),
            (0x38, "Left Alt"), (0xE038, "Right Alt"), (0xE05B, "Left Win"), (0xE05C, "Right Win"), (0xE05D, "Menu"),
        });
        return keys;
    }

    private static void AddRow(List<(int, string)> keys, int firstCode, string letters)
    {
        for (var i = 0; i < letters.Length; i++)
            keys.Add((firstCode + i, letters[i].ToString()));
    }
}
