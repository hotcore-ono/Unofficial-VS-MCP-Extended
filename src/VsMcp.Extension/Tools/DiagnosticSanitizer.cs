using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended (Phase 10): 機密情報を通常ログへ出さないための変換（指示書 §16〜§18・§25・§31）。
    /// Tool 引数は raw のまま保存せず、長さ・有無・種別へ要約する。fileName / path / title / name / message は
    /// 既定では本文を出さず、includeUiText / includeFilePaths が true のときだけ本文を許可する。
    /// keys / text（打鍵される入力そのもの）は設定に関わらず本文を出さず、有無と長さだけを残す。
    /// </summary>
    internal static class DiagnosticSanitizer
    {
        /// <summary>値を伏せるキー名（部分一致・大小無視）。</summary>
        private static readonly string[] _SENSITIVE_KEY_WORDS =
        {
            "password", "passphrase", "secret", "token", "apikey", "authorization", "cookie", "credential", "privatekey",
        };

        /// <summary>伏せた値の代わりに入れる文字列。</summary>
        private const string _REDACTED = "***";

        /// <summary>Windows のパスを置き換える文字列。</summary>
        private const string _PATH_PLACEHOLDER = "<path>";

        /// <summary>そのまま記録してよい引数キー（識別子・列挙値であり機密になり得ない）。</summary>
        private static readonly HashSet<string> _PLAIN_STRING_KEYS = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "automationId", "sourceAutomationId", "targetAutomationId", "ancestorAutomationId",
            "className", "sourceClassName", "targetClassName",
            "controlType", "sourceControlType", "targetControlType",
            "action", "method", "mode", "view", "nameMatch", "automationIdMatch", "classNameMatch",
            "hasPattern", "itemId", "dialogType", "button", "level", "pane",
        };

        /// <summary>
        /// UI テキスト扱いの引数キー（includeUiText が true のときだけ本文を出す）。
        /// "text" は入力そのもの（ui_window_send_keys / set_filename）なので、ここには入れず keys と同じ扱いにする。
        /// </summary>
        private static readonly HashSet<string> _UI_TEXT_KEYS = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "sourceName", "targetName", "title", "titleContains", "message",
        };

        /// <summary>ファイルパス扱いの引数キー（includeFilePaths が true のときだけ本文を出す）。</summary>
        private static readonly HashSet<string> _FILE_PATH_KEYS = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "fileName", "path", "folder", "directory",
        };

        /// <summary>機密語を含む key=value / key: value / "key":"value" の value を捕まえる正規表現。</summary>
        private static readonly Regex _SENSITIVE_ASSIGNMENT_PATTERN = new Regex(
            "(?<key>password|passphrase|secret|token|apikey|api_key|authorization|cookie|credential|privatekey|private_key)"
            + "(?<separator>\"?\\s*[:=]\\s*\"?)(?<value>[^\"\\s,;}]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Windows のローカルパス（X:\...）と UNC パス（\\server\...）を捕まえる正規表現。</summary>
        private static readonly Regex _WINDOWS_PATH_PATTERN = new Regex(
            "([A-Za-z]:\\\\[^\\s\"'<>|]*|\\\\\\\\[^\\s\"'<>|]+\\\\[^\\s\"'<>|]*)",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// Tool 引数を診断ログ用に要約する。raw JSON は保存せず、数値 / bool はそのまま、文字列は種別ごとに長さや有無へ落とす。
        /// </summary>
        /// <param name="InToolName">呼び出された Tool 名（現状は要約の分岐に使わないが、将来の Tool 固有規則のために受け取る）。</param>
        /// <param name="InArgs">Tool 引数。null なら空の要約を返す。</param>
        /// <param name="InSettings">機密ポリシーの設定。</param>
        /// <returns>要約した JObject。</returns>
        public static JObject SummarizeArguments(string InToolName, JObject InArgs, DiagnosticSettings InSettings)
        {
            JObject TheSummary = new JObject();
            if (InArgs == null)
            {
                return TheSummary;
            }

            foreach (KeyValuePair<string, JToken> ThePair in InArgs)
            {
                try
                {
                    SummarizeArgument(ThePair.Key, ThePair.Value, InSettings, TheSummary);
                }
                catch (Exception)
                {
                    // 1 引数の要約に失敗しても他の引数は残す
                    TheSummary[ThePair.Key + "Summarized"] = false;
                }
            }
            return TheSummary;
        }

        /// <summary>引数 1 件を種別に応じて要約し、結果へ書き込む。</summary>
        /// <param name="InKey">引数のキー名。</param>
        /// <param name="InValue">引数の値。</param>
        /// <param name="InSettings">機密ポリシーの設定。</param>
        /// <param name="InOutSummary">書き込み先の要約。</param>
        private static void SummarizeArgument(string InKey, JToken InValue, DiagnosticSettings InSettings, JObject InOutSummary)
        {
            if (InValue == null || InValue.Type == JTokenType.Null)
            {
                return;
            }

            if (IsSensitiveKey(InKey))
            {
                InOutSummary[InKey] = _REDACTED;
                return;
            }

            if (InValue.Type == JTokenType.Integer || InValue.Type == JTokenType.Float || InValue.Type == JTokenType.Boolean)
            {
                InOutSummary[InKey] = InValue;
                return;
            }

            if (InValue.Type == JTokenType.Array)
            {
                InOutSummary[InKey + "Count"] = ((JArray)InValue).Count;
                return;
            }

            if (InValue.Type == JTokenType.Object)
            {
                InOutSummary[InKey + "IsObject"] = true;
                return;
            }

            string TheText = InValue.Value<string>() ?? string.Empty;

            if (string.Equals(InKey, "keys", StringComparison.OrdinalIgnoreCase))
            {
                // ui_window_send_keys の keys は本文を出さず、指定の有無とトークン数だけ残す（指示書 §17）
                InOutSummary["keysProvided"] = true;
                InOutSummary["keysTokenCount"] = CountKeyTokens(TheText);
                return;
            }

            if (string.Equals(InKey, "text", StringComparison.OrdinalIgnoreCase))
            {
                // text は打鍵される入力そのもの（パスワードにもなり得る）なので、includeUiText でも本文は出さない（指示書 §17）
                InOutSummary["textProvided"] = true;
                InOutSummary["textLength"] = TheText.Length;
                return;
            }

            if (_PLAIN_STRING_KEYS.Contains(InKey))
            {
                InOutSummary[InKey] = Truncate(TheText, InSettings);
                return;
            }

            if (_FILE_PATH_KEYS.Contains(InKey))
            {
                InOutSummary["has" + Capitalize(InKey)] = TheText.Length > 0;
                InOutSummary[InKey + "Length"] = TheText.Length;
                InOutSummary[InKey + "PathKind"] = DescribePathKind(TheText);
                InOutSummary[InKey + "IsAbsolute"] = IsAbsolutePath(TheText);
                if (InSettings != null && InSettings.IsFilePathsIncluded)
                {
                    InOutSummary[InKey] = Truncate(TheText, InSettings);
                }
                return;
            }

            if (_UI_TEXT_KEYS.Contains(InKey))
            {
                InOutSummary["has" + Capitalize(InKey)] = TheText.Length > 0;
                InOutSummary[InKey + "Length"] = TheText.Length;
                if (InSettings != null && InSettings.IsUiTextIncluded)
                {
                    InOutSummary[InKey] = Truncate(SanitizeText(TheText, InSettings), InSettings);
                }
                return;
            }

            // 未知の文字列キーは本文を出さず長さだけ残す（新しい Tool 引数が増えても既定で漏らさない）
            InOutSummary[InKey + "Length"] = TheText.Length;
        }

        /// <summary>キー名が機密語を含むか判定する（部分一致・大小無視）。</summary>
        /// <param name="InKey">判定するキー名。</param>
        /// <returns>機密語を含むなら true。</returns>
        public static bool IsSensitiveKey(string InKey)
        {
            if (string.IsNullOrEmpty(InKey))
            {
                return false;
            }
            string TheNormalized = InKey.Replace("_", string.Empty).ToLowerInvariant();
            foreach (string TheWord in _SENSITIVE_KEY_WORDS)
            {
                if (TheNormalized.IndexOf(TheWord, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 自由文（例外メッセージ・Tool のエラー文・marker のメッセージ）を診断ログ用に整える。
        /// 機密語の代入部分を伏せ、includeFilePaths が false なら Windows パスを &lt;path&gt; へ置き換え、最大長で切る。
        /// </summary>
        /// <param name="InText">元の文字列。null なら null を返す。</param>
        /// <param name="InSettings">機密ポリシーの設定。null なら既定の上限で切るだけ。</param>
        /// <returns>整えた文字列。伏せ字化に失敗した場合は "***"。</returns>
        public static string SanitizeText(string InText, DiagnosticSettings InSettings)
        {
            if (string.IsNullOrEmpty(InText))
            {
                return InText;
            }

            string TheResult = InText;
            try
            {
                TheResult = _SENSITIVE_ASSIGNMENT_PATTERN.Replace(TheResult, InMatch =>
                    InMatch.Groups["key"].Value + InMatch.Groups["separator"].Value + _REDACTED);
                if (InSettings == null || !InSettings.IsFilePathsIncluded)
                {
                    TheResult = _WINDOWS_PATH_PATTERN.Replace(TheResult, _PATH_PLACEHOLDER);
                }
            }
            catch (Exception)
            {
                // 伏せ字化できなかった文字列は原文へ戻さない（機密が素通りするより情報を落とす方を選ぶ）
                return _REDACTED;
            }
            return Truncate(TheResult, InSettings);
        }

        /// <summary>
        /// UIA 要素の Name / Value を記録してよいか判定する。includeUiText が false のとき、
        /// および パスワード系（AutomationId / Name に "password" を含む、ControlType.Edit の伏字入力）は常に出さない。
        /// </summary>
        /// <param name="InAutomationId">要素の AutomationId。</param>
        /// <param name="InName">要素の Name。</param>
        /// <param name="InSettings">機密ポリシーの設定。</param>
        /// <returns>本文を記録してよいなら true。</returns>
        public static bool CanIncludeElementText(string InAutomationId, string InName, DiagnosticSettings InSettings)
        {
            if (InSettings == null || !InSettings.IsUiTextIncluded)
            {
                return false;
            }
            return !IsSensitiveKey(InAutomationId) && !IsSensitiveKey(InName);
        }

        /// <summary>
        /// UI テキストを設定に従って記録用の値にする。許可されていなければ null を返す（呼び出し側は長さだけ載せる）。
        /// </summary>
        /// <param name="InText">UI から読んだテキスト。</param>
        /// <param name="InSettings">機密ポリシーの設定。</param>
        /// <returns>記録してよい文字列。許可されていなければ null。</returns>
        public static string SanitizeUiText(string InText, DiagnosticSettings InSettings)
        {
            if (InSettings == null || !InSettings.IsUiTextIncluded || InText == null)
            {
                return null;
            }
            return SanitizeText(InText, InSettings);
        }

        /// <summary>パスの種別を判定する（本文を出さずに形だけ残すため）。</summary>
        /// <param name="InPath">判定するパス文字列。</param>
        /// <returns>"empty" / "unc" / "drive" / "rooted" / "relative"。</returns>
        public static string DescribePathKind(string InPath)
        {
            if (string.IsNullOrEmpty(InPath))
            {
                return "empty";
            }
            if (InPath.StartsWith("\\\\", StringComparison.Ordinal))
            {
                return "unc";
            }
            if (InPath.Length >= 2 && InPath[1] == ':')
            {
                return "drive";
            }
            if (InPath[0] == '\\' || InPath[0] == '/')
            {
                return "rooted";
            }
            return "relative";
        }

        /// <summary>パスが絶対パスか判定する（例外を投げない簡易判定）。</summary>
        /// <param name="InPath">判定するパス文字列。</param>
        /// <returns>絶対パスなら true。</returns>
        public static bool IsAbsolutePath(string InPath)
        {
            string TheKind = DescribePathKind(InPath);
            return string.Equals(TheKind, "unc", StringComparison.Ordinal) || string.Equals(TheKind, "drive", StringComparison.Ordinal);
        }

        /// <summary>設定の最大長で文字列を切り、切った場合は "…" を付ける。</summary>
        /// <param name="InText">元の文字列。</param>
        /// <param name="InSettings">最大長を持つ設定。null なら 200 字。</param>
        /// <returns>切った後の文字列。</returns>
        public static string Truncate(string InText, DiagnosticSettings InSettings)
        {
            if (string.IsNullOrEmpty(InText))
            {
                return InText;
            }
            int TheLimit = InSettings == null ? 200 : InSettings.MaxStringLength;
            if (InText.Length <= TheLimit)
            {
                return InText;
            }
            return InText.Substring(0, TheLimit) + "…";
        }

        /// <summary>ui_window_send_keys の keys を空白区切りのトークン数として数える。</summary>
        /// <param name="InKeys">keys 引数の本文。</param>
        /// <returns>トークン数。</returns>
        private static int CountKeyTokens(string InKeys)
        {
            if (string.IsNullOrWhiteSpace(InKeys))
            {
                return 0;
            }
            return InKeys.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        /// <summary>"fileName" → "FileName" のように先頭を大文字にする（hasX キーの組み立て用）。</summary>
        /// <param name="InText">元の文字列。</param>
        /// <returns>先頭を大文字にした文字列。</returns>
        private static string Capitalize(string InText)
        {
            if (string.IsNullOrEmpty(InText))
            {
                return InText;
            }
            return char.ToUpperInvariant(InText[0]) + InText.Substring(1);
        }
    }
}
