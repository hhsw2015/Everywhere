// Mirrors: packages/OpenComputerUseKit/Sources/OpenComputerUseKit/KeyMapping.swift
// Upstream: iFurySt/open-codex-computer-use@<sha-pinned-in-UPSTREAM_REF.md>
//
// Maps xdotool-style key names to macOS Carbon virtual-key codes (kVK_*).
// Constants come from <Carbon/Events.h>; not re-exported by .NET so we
// inline them as a const table.

namespace Everywhere.Mac.Mcp;

internal static class MacKeyCodes
{
    // Modifier flag masks from CGEventFlags.
    public const ulong MaskShift = 0x00020000;
    public const ulong MaskControl = 0x00040000;
    public const ulong MaskAlternate = 0x00080000;
    public const ulong MaskCommand = 0x00100000;

    // From <Carbon/HIToolbox/Events.h> — kVK_* values.
    public const ushort VkAnsiA = 0x00, VkAnsiB = 0x0B, VkAnsiC = 0x08, VkAnsiD = 0x02;
    public const ushort VkAnsiE = 0x0E, VkAnsiF = 0x03, VkAnsiG = 0x05, VkAnsiH = 0x04;
    public const ushort VkAnsiI = 0x22, VkAnsiJ = 0x26, VkAnsiK = 0x28, VkAnsiL = 0x25;
    public const ushort VkAnsiM = 0x2E, VkAnsiN = 0x2D, VkAnsiO = 0x1F, VkAnsiP = 0x23;
    public const ushort VkAnsiQ = 0x0C, VkAnsiR = 0x0F, VkAnsiS = 0x01, VkAnsiT = 0x11;
    public const ushort VkAnsiU = 0x20, VkAnsiV = 0x09, VkAnsiW = 0x0D, VkAnsiX = 0x07;
    public const ushort VkAnsiY = 0x10, VkAnsiZ = 0x06;
    public const ushort VkAnsi0 = 0x1D, VkAnsi1 = 0x12, VkAnsi2 = 0x13, VkAnsi3 = 0x14;
    public const ushort VkAnsi4 = 0x15, VkAnsi5 = 0x17, VkAnsi6 = 0x16, VkAnsi7 = 0x1A;
    public const ushort VkAnsi8 = 0x1C, VkAnsi9 = 0x19;

    public const ushort VkReturn = 0x24, VkTab = 0x30, VkSpace = 0x31, VkEscape = 0x35;
    public const ushort VkDelete = 0x33, VkForwardDelete = 0x75, VkHelp = 0x72;
    public const ushort VkUpArrow = 0x7E, VkDownArrow = 0x7D, VkLeftArrow = 0x7B, VkRightArrow = 0x7C;
    public const ushort VkHome = 0x73, VkEnd = 0x77, VkPageUp = 0x74, VkPageDown = 0x79;
    public const ushort VkCapsLock = 0x39;

    public const ushort VkF1 = 0x7A, VkF2 = 0x78, VkF3 = 0x63, VkF4 = 0x76;
    public const ushort VkF5 = 0x60, VkF6 = 0x61, VkF7 = 0x62, VkF8 = 0x64;
    public const ushort VkF9 = 0x65, VkF10 = 0x6D, VkF11 = 0x67, VkF12 = 0x6F;

    public const ushort VkKeypad0 = 0x52, VkKeypad1 = 0x53, VkKeypad2 = 0x54, VkKeypad3 = 0x55;
    public const ushort VkKeypad4 = 0x56, VkKeypad5 = 0x57, VkKeypad6 = 0x58, VkKeypad7 = 0x59;
    public const ushort VkKeypad8 = 0x5B, VkKeypad9 = 0x5C;
    public const ushort VkKeypadEnter = 0x4C, VkKeypadEquals = 0x51, VkKeypadMultiply = 0x43;
    public const ushort VkKeypadPlus = 0x45, VkKeypadMinus = 0x4E, VkKeypadDecimal = 0x41;
    public const ushort VkKeypadDivide = 0x4B;

    public const ushort VkCommand = 0x37, VkShift = 0x38, VkOption = 0x3A, VkControl = 0x3B;

    public static readonly Dictionary<string, ushort> KeyByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = VkAnsiA, ["b"] = VkAnsiB, ["c"] = VkAnsiC, ["d"] = VkAnsiD,
        ["e"] = VkAnsiE, ["f"] = VkAnsiF, ["g"] = VkAnsiG, ["h"] = VkAnsiH,
        ["i"] = VkAnsiI, ["j"] = VkAnsiJ, ["k"] = VkAnsiK, ["l"] = VkAnsiL,
        ["m"] = VkAnsiM, ["n"] = VkAnsiN, ["o"] = VkAnsiO, ["p"] = VkAnsiP,
        ["q"] = VkAnsiQ, ["r"] = VkAnsiR, ["s"] = VkAnsiS, ["t"] = VkAnsiT,
        ["u"] = VkAnsiU, ["v"] = VkAnsiV, ["w"] = VkAnsiW, ["x"] = VkAnsiX,
        ["y"] = VkAnsiY, ["z"] = VkAnsiZ,
        ["0"] = VkAnsi0, ["1"] = VkAnsi1, ["2"] = VkAnsi2, ["3"] = VkAnsi3,
        ["4"] = VkAnsi4, ["5"] = VkAnsi5, ["6"] = VkAnsi6, ["7"] = VkAnsi7,
        ["8"] = VkAnsi8, ["9"] = VkAnsi9,

        ["return"] = VkReturn, ["enter"] = VkReturn,
        ["tab"] = VkTab,
        ["space"] = VkSpace, ["spacebar"] = VkSpace,
        ["escape"] = VkEscape, ["esc"] = VkEscape,
        ["backspace"] = VkDelete, ["delete"] = VkDelete,
        ["del"] = VkForwardDelete, ["forwarddelete"] = VkForwardDelete,
        ["insert"] = VkHelp,

        ["up"] = VkUpArrow, ["down"] = VkDownArrow, ["left"] = VkLeftArrow, ["right"] = VkRightArrow,
        ["home"] = VkHome, ["end"] = VkEnd,
        ["pageup"] = VkPageUp, ["page_up"] = VkPageUp, ["prior"] = VkPageUp,
        ["pagedown"] = VkPageDown, ["page_down"] = VkPageDown, ["next"] = VkPageDown,
        ["caps_lock"] = VkCapsLock,

        ["f1"] = VkF1, ["f2"] = VkF2, ["f3"] = VkF3, ["f4"] = VkF4,
        ["f5"] = VkF5, ["f6"] = VkF6, ["f7"] = VkF7, ["f8"] = VkF8,
        ["f9"] = VkF9, ["f10"] = VkF10, ["f11"] = VkF11, ["f12"] = VkF12,

        ["kp_0"] = VkKeypad0, ["kp_1"] = VkKeypad1, ["kp_2"] = VkKeypad2, ["kp_3"] = VkKeypad3,
        ["kp_4"] = VkKeypad4, ["kp_5"] = VkKeypad5, ["kp_6"] = VkKeypad6, ["kp_7"] = VkKeypad7,
        ["kp_8"] = VkKeypad8, ["kp_9"] = VkKeypad9,
        ["kp_enter"] = VkKeypadEnter, ["kp_equal"] = VkKeypadEquals, ["kp_multiply"] = VkKeypadMultiply,
        ["kp_add"] = VkKeypadPlus, ["kp_subtract"] = VkKeypadMinus, ["kp_decimal"] = VkKeypadDecimal,
        ["kp_divide"] = VkKeypadDivide, ["kp_delete"] = VkKeypadDecimal,
        ["kp_home"] = VkHome, ["kp_left"] = VkLeftArrow, ["kp_up"] = VkUpArrow,
        ["kp_right"] = VkRightArrow, ["kp_down"] = VkDownArrow,
        ["kp_prior"] = VkPageUp, ["kp_page_up"] = VkPageUp,
        ["kp_next"] = VkPageDown, ["kp_page_down"] = VkPageDown,
        ["kp_end"] = VkEnd, ["kp_insert"] = VkHelp,
    };

    public static readonly Dictionary<string, (ulong Flag, ushort KeyCode)> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cmd"] = (MaskCommand, VkCommand),
        ["command"] = (MaskCommand, VkCommand),
        ["super"] = (MaskCommand, VkCommand),
        ["meta"] = (MaskCommand, VkCommand),
        ["shift"] = (MaskShift, VkShift),
        ["option"] = (MaskAlternate, VkOption),
        ["alt"] = (MaskAlternate, VkOption),
        ["control"] = (MaskControl, VkControl),
        ["ctrl"] = (MaskControl, VkControl),
    };
}
