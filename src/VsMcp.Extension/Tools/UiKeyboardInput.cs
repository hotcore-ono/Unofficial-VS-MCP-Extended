using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using static VsMcp.Extension.Tools.NativeMethods;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended: キーボード入力の共有層。キー文法は upstream ui_send_keys と完全に同じ（空白区切りの並び、"ctrl+f" のような
    /// 組み合わせ、ctrl / control / shift / alt / win / windows の修飾、<see cref="NativeMethods.NamedKeys"/> の名前付きキー、
    /// 1 文字は VkKeyScan で仮想キーと shift 状態へ変換）で、待ち時間も upstream と同じ（キー 30 ms / 文字 10 ms）にする。
    /// ただし注入そのものは upstream の <c>UiTools.SendKeyCombo</c> / <c>SendCharacter</c> を呼ばず Extended 側で実装する。
    /// 理由: upstream の <see cref="NativeMethods.INPUT"/> は union が KEYBDINPUT だけで x64 では 32 バイトにしかならず、
    /// Win32 が要求する 40 バイトと一致しないため、<c>SendInput(..., Marshal.SizeOf(typeof(INPUT)))</c> は
    /// 0 を返して（GetLastError=87 ERROR_INVALID_PARAMETER）1 イベントも注入しない（2026-09-09 実測。upstream への Issue 候補）。
    /// そのため正しいレイアウトの <see cref="NativeMethods.INPUT_EX"/> と <see cref="NativeMethods.SendInputEx"/> を使う。
    /// 安全境界（対象ウィンドウの検証・モーダル判定・フォアグラウンド判定）はここには置かず、呼び出し側のツールが持つ。
    /// </summary>
    internal static class UiKeyboardInput
    {
        /// <summary>キーの並びを送るときの 1 個ごとの待ち時間（upstream ui_send_keys と同じ 30 ms）。</summary>
        private const int _KEY_COMBO_INTERVAL_MS = 30;

        /// <summary>文字列を 1 文字ずつ送るときの待ち時間（upstream ui_send_keys と同じ 10 ms）。</summary>
        private const int _TEXT_CHARACTER_INTERVAL_MS = 10;

        /// <summary>ポップアップメニューを取り下げるために送る ESC のキー名（upstream の名前付きキー表と同じ表記）。</summary>
        private const string _ESCAPE_KEY_NAME = "escape";

        /// <summary>組み合わせを分割する区切り文字（"ctrl+shift+s"）。</summary>
        private const char _COMBO_SEPARATOR = '+';

        /// <summary>VkKeyScan の戻り値の下位バイト（仮想キー）を取り出すマスク。</summary>
        private const int _VIRTUAL_KEY_MASK = 0xFF;

        /// <summary>VkKeyScan の shift 状態: Shift。</summary>
        private const int _SHIFT_STATE_SHIFT = 1;

        /// <summary>VkKeyScan の shift 状態: Ctrl。</summary>
        private const int _SHIFT_STATE_CONTROL = 2;

        /// <summary>VkKeyScan の shift 状態: Alt。</summary>
        private const int _SHIFT_STATE_ALT = 4;

        /// <summary>
        /// キーの並び（keys）と文字列（text）を、この順で送る。どちらも空なら何もしない。
        /// keys は空白で分割して 1 組み合わせずつ送り（2 個以上のときだけ間に 30 ms を挟む）、text は 1 文字ずつ 10 ms 間隔で送る。
        /// フォアグラウンドの制御は行わないので、呼び出し側が送信先を確定させてから呼ぶこと。
        /// </summary>
        /// <param name="InKeys">送るキーの並び（例: "escape"、"ctrl+s"、"tab tab enter"）。空なら送らない。</param>
        /// <param name="InText">送る文字列。空なら送らない。</param>
        /// <returns>実際に注入したイベント数（キーの down / up がそれぞれ 1 件）。</returns>
        public static int SendKeys(string InKeys, string InText)
        {
            int TheInsertedEvents = 0;
            if (!string.IsNullOrEmpty(InKeys))
            {
                string[] TheSequence = InKeys.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string TheCombo in TheSequence)
                {
                    TheInsertedEvents += SendKeyCombo(TheCombo);
                    if (TheSequence.Length > 1)
                    {
                        Thread.Sleep(_KEY_COMBO_INTERVAL_MS);
                    }
                }
            }

            if (!string.IsNullOrEmpty(InText))
            {
                foreach (char TheCharacter in InText)
                {
                    TheInsertedEvents += SendCharacter(TheCharacter);
                    Thread.Sleep(_TEXT_CHARACTER_INTERVAL_MS);
                }
            }
            return TheInsertedEvents;
        }

        /// <summary>
        /// ESC を 1 回送る（SendInput 経由の物理キー入力。keybd_event は使わない）。
        /// 開いているポップアップメニューはフォアグラウンドを持たないので、送信先の切り替えは行わない。
        /// </summary>
        /// <returns>実際に注入したイベント数（down / up の 2 件）。</returns>
        public static int SendEscape()
        {
            return SendKeyCombo(_ESCAPE_KEY_NAME);
        }

        /// <summary>
        /// 1 つのキー組み合わせ（"ctrl+shift+s" など）を、修飾キー down → 主キー down / up → 修飾キー up（逆順）の順に送る。
        /// 解釈できるものが 1 つも無い組み合わせは何も送らない（upstream と同じ扱い）。
        /// </summary>
        /// <param name="InCombo">キー組み合わせの文字列。</param>
        /// <returns>実際に注入したイベント数。</returns>
        private static int SendKeyCombo(string InCombo)
        {
            List<ushort> TheModifiers = new List<ushort>();
            ushort TheMainKey = 0;
            string[] TheParts = InCombo.Split(new[] { _COMBO_SEPARATOR }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string ThePart in TheParts)
            {
                string TheKeyName = ThePart.Trim().ToLowerInvariant();
                if (string.Equals(TheKeyName, "ctrl", StringComparison.Ordinal) || string.Equals(TheKeyName, "control", StringComparison.Ordinal))
                {
                    TheModifiers.Add(VK_CONTROL);
                }
                else if (string.Equals(TheKeyName, "shift", StringComparison.Ordinal))
                {
                    TheModifiers.Add(VK_SHIFT);
                }
                else if (string.Equals(TheKeyName, "alt", StringComparison.Ordinal))
                {
                    TheModifiers.Add(VK_MENU);
                }
                else if (string.Equals(TheKeyName, "win", StringComparison.Ordinal) || string.Equals(TheKeyName, "windows", StringComparison.Ordinal))
                {
                    TheModifiers.Add(VK_LWIN);
                }
                else if (NamedKeys.TryGetValue(TheKeyName, out ushort TheNamedKey))
                {
                    TheMainKey = TheNamedKey;
                }
                else if (TheKeyName.Length == 1)
                {
                    TheMainKey = ResolveSingleCharacterKey(TheKeyName[0]);
                }
            }

            if (TheMainKey == 0 && TheModifiers.Count == 0)
            {
                return 0;
            }

            List<INPUT_EX> TheInputs = new List<INPUT_EX>();
            foreach (ushort TheModifier in TheModifiers)
            {
                TheInputs.Add(MakeKeyInput(TheModifier, false));
            }
            if (TheMainKey != 0)
            {
                TheInputs.Add(MakeKeyInput(TheMainKey, false));
                TheInputs.Add(MakeKeyInput(TheMainKey, true));
            }
            for (int TheIndex = TheModifiers.Count - 1; TheIndex >= 0; TheIndex--)
            {
                TheInputs.Add(MakeKeyInput(TheModifiers[TheIndex], true));
            }
            return SendInputEvents(TheInputs);
        }

        /// <summary>
        /// 1 文字を送る。VkKeyScan で仮想キーと shift 状態へ変換し、必要な修飾キーを添えて down / up を送る（upstream と同じ）。
        /// 現在のキーボードレイアウトで打てない文字は何も送らない。
        /// </summary>
        /// <param name="InCharacter">送る文字。</param>
        /// <returns>実際に注入したイベント数。</returns>
        private static int SendCharacter(char InCharacter)
        {
            short TheScanResult = VkKeyScan(InCharacter);
            if (TheScanResult == -1)
            {
                // 現在のレイアウトへ割り当てられない文字は送らない（upstream と同じ扱い）
                return 0;
            }

            ushort TheVirtualKey = (ushort)(TheScanResult & _VIRTUAL_KEY_MASK);
            int TheShiftState = (TheScanResult >> 8) & _VIRTUAL_KEY_MASK;
            List<INPUT_EX> TheInputs = new List<INPUT_EX>();
            if ((TheShiftState & _SHIFT_STATE_SHIFT) != 0)
            {
                TheInputs.Add(MakeKeyInput(VK_SHIFT, false));
            }
            if ((TheShiftState & _SHIFT_STATE_CONTROL) != 0)
            {
                TheInputs.Add(MakeKeyInput(VK_CONTROL, false));
            }
            if ((TheShiftState & _SHIFT_STATE_ALT) != 0)
            {
                TheInputs.Add(MakeKeyInput(VK_MENU, false));
            }

            TheInputs.Add(MakeKeyInput(TheVirtualKey, false));
            TheInputs.Add(MakeKeyInput(TheVirtualKey, true));

            if ((TheShiftState & _SHIFT_STATE_ALT) != 0)
            {
                TheInputs.Add(MakeKeyInput(VK_MENU, true));
            }
            if ((TheShiftState & _SHIFT_STATE_CONTROL) != 0)
            {
                TheInputs.Add(MakeKeyInput(VK_CONTROL, true));
            }
            if ((TheShiftState & _SHIFT_STATE_SHIFT) != 0)
            {
                TheInputs.Add(MakeKeyInput(VK_SHIFT, true));
            }
            return SendInputEvents(TheInputs);
        }

        /// <summary>
        /// 組み合わせの中の 1 文字を仮想キーへ変換する（A-Z / 0-9 は ASCII と同じ値、それ以外は VkKeyScan の下位バイト）。
        /// </summary>
        /// <param name="InCharacter">変換する 1 文字（小文字化済み）。</param>
        /// <returns>仮想キーコード。変換できなければ 0。</returns>
        private static ushort ResolveSingleCharacterKey(char InCharacter)
        {
            char TheUpperCharacter = char.ToUpperInvariant(InCharacter);
            if ((TheUpperCharacter >= 'A' && TheUpperCharacter <= 'Z') || (TheUpperCharacter >= '0' && TheUpperCharacter <= '9'))
            {
                // VK_A..VK_Z / VK_0..VK_9 は ASCII コードと同じ値
                return (ushort)TheUpperCharacter;
            }
            short TheScanResult = VkKeyScan(InCharacter);
            if (TheScanResult == -1)
            {
                return 0;
            }
            return (ushort)(TheScanResult & _VIRTUAL_KEY_MASK);
        }

        /// <summary>1 件のキーイベントを組み立てる（scan code は MapVirtualKey で解決する。upstream と同じ内容）。</summary>
        /// <param name="InVirtualKey">仮想キーコード。</param>
        /// <param name="InIsKeyUp">離す方向なら true。</param>
        /// <returns>SendInput へ渡すイベント。</returns>
        private static INPUT_EX MakeKeyInput(ushort InVirtualKey, bool InIsKeyUp)
        {
            ushort TheScanCode = (ushort)MapVirtualKey(InVirtualKey, MAPVK_VK_TO_VSC);
            return new INPUT_EX
            {
                type = INPUT_KEYBOARD,
                union = new INPUTUNION_EX
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = InVirtualKey,
                        wScan = TheScanCode,
                        dwFlags = InIsKeyUp ? KEYEVENTF_KEYUP : 0,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero,
                    },
                },
            };
        }

        /// <summary>
        /// 組み立てたイベントを SendInput へ渡す。戻り値が要求数に満たない場合は「注入されていない」ので、
        /// Win32 のエラーコードを添えて例外にする（呼び出し側のツールが「キーを送っていない」と報告できるようにするため）。
        /// </summary>
        /// <param name="InInputs">送るイベントの一覧。空なら何もしない。</param>
        /// <returns>注入できたイベント数。</returns>
        private static int SendInputEvents(List<INPUT_EX> InInputs)
        {
            if (InInputs.Count == 0)
            {
                return 0;
            }
            INPUT_EX[] TheInputArray = InInputs.ToArray();
            uint TheInsertedEvents = SendInputEx((uint)TheInputArray.Length, TheInputArray, Marshal.SizeOf(typeof(INPUT_EX)));
            if (TheInsertedEvents < (uint)TheInputArray.Length)
            {
                int TheWin32Error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"SendInput inserted {TheInsertedEvents} of {TheInputArray.Length} events (win32 error {TheWin32Error})");
            }
            return (int)TheInsertedEvents;
        }
    }
}
