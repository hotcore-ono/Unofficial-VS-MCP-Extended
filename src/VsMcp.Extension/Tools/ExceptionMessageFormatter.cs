using System;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended: Tool のエラー文へ例外の HRESULT と型名を付ける整形。
    /// COM / UI Automation の例外は Message が "Unrecognized error." のように原因を表さないことがあり、
    /// Tool がエラー結果へ変換して握ると診断ログにも HRESULT が残らない。そのため、エラー文の末尾へ括弧書きで足す。
    /// 例外の Message 本文は変えない（先頭と本文は元のまま、末尾に括弧書きが付くだけ）。
    /// </summary>
    internal static class ExceptionMessageFormatter
    {
        /// <summary>HResult の既定値 COR_E_EXCEPTION（0x80131500）。明示的に HRESULT が設定されていない例外はこの値になる。</summary>
        private const int _DEFAULT_EXCEPTION_HRESULT = unchecked((int)0x80131500);

        /// <summary>
        /// 例外の Message に HRESULT と例外型名を付けた文字列を返す。
        /// HResult が 0 または既定値（0x80131500）のときは情報が無いので型名だけを付ける。
        /// Message が空のときは先頭の空白を付けず、括弧書きだけを返す。
        /// </summary>
        /// <param name="InException">整形する例外。null は不可。</param>
        /// <returns>"Message (HRESULT 0x80004005, COMException)" または "Message (Exception)" の形の文字列。型名だけになるのは HResult が 0 または 0x80131500 のときだけで、
        /// InvalidOperationException（0x80131509）など型固有の既定 HResult を持つ例外には HRESULT も付く。</returns>
        /// <exception cref="ArgumentNullException">InException が null の場合。</exception>
        public static string FormatWithHResult(Exception InException)
        {
            if (InException == null)
            {
                throw new ArgumentNullException(nameof(InException));
            }

            string TheTypeName = InException.GetType().Name;
            string TheSuffix = InException.HResult == 0 || InException.HResult == _DEFAULT_EXCEPTION_HRESULT
                ? "(" + TheTypeName + ")"
                : "(HRESULT 0x" + InException.HResult.ToString("X8") + ", " + TheTypeName + ")";

            if (string.IsNullOrEmpty(InException.Message))
            {
                return TheSuffix;
            }
            return InException.Message + " " + TheSuffix;
        }
    }
}
