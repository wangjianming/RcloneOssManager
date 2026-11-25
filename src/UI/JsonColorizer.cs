using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RcloneOssManager
{
    /// <summary>JSON 正文的着色类别。</summary>
    internal enum JsonTokenKind { Key, Text, Risk, Number, Punct }

    /// <summary>一段需要统一着色的正文区间。</summary>
    internal sealed class JsonToken
    {
        internal JsonToken(int start, int length, JsonTokenKind kind)
        {
            Start = start;
            Length = length;
            Kind = kind;
        }

        internal readonly int Start;
        internal readonly int Length;
        internal readonly JsonTokenKind Kind;
    }

    /// <summary>把将要写入的 Bucket Policy 正文按语义着色：键名、普通取值、需要警惕的通配与 Deny、数字与标点分色，只读区也读得懂。</summary>
    internal static class JsonColorizer
    {
        /// <summary>逐段扫描 JSON 文本，返回需要配色的区间；引号不闭合时后面的内容一律不上色，避免把半截字符串当成取值。</summary>
        /// <param name="text">待着色的 JSON 正文。</param>
        internal static List<JsonToken> Scan(string text)
        {
            var tokens = new List<JsonToken>();
            if (text == null) return tokens;
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '"')
                {
                    var end = i + 1;
                    while (end < text.Length && text[end] != '"') end += text[end] == '\\' ? 2 : 1;
                    if (end >= text.Length) break;
                    var after = end + 1;
                    while (after < text.Length && (text[after] == ' ' || text[after] == '\t')) after++;
                    var value = text.Substring(i + 1, end - i - 1);
                    var kind = after < text.Length && text[after] == ':' ? JsonTokenKind.Key
                        : IsRisk(value) ? JsonTokenKind.Risk : JsonTokenKind.Text;
                    tokens.Add(new JsonToken(i, end + 1 - i, kind));
                    i = end + 1;
                    continue;
                }
                if (c == '{' || c == '}' || c == '[' || c == ']' || c == ',' || c == ':')
                {
                    tokens.Add(new JsonToken(i, 1, JsonTokenKind.Punct));
                    i++;
                    continue;
                }
                if (c == '-' || (c >= '0' && c <= '9') || c == 't' || c == 'f' || c == 'n')
                {
                    var start = i;
                    while (i < text.Length && !IsBreak(text[i])) i++;
                    tokens.Add(new JsonToken(start, i - start, JsonTokenKind.Number));
                    continue;
                }
                i++;
            }
            return tokens;
        }

        /// <summary>取值是否值得标红：匿名通配、任意动作、显式 Deny 都是改完最难追回的那几类。</summary>
        /// <param name="value">去掉引号后的字符串取值。</param>
        internal static bool IsRisk(string value)
        {
            if (value == "*") return true;
            if (value == "Deny") return true;
            return value.Length > 2 && value[value.Length - 1] == '*' && value[value.Length - 2] == ':';
        }

        /// <summary>按类别取配色，界面与用例共用同一张表。</summary>
        /// <param name="kind">着色类别。</param>
        internal static Color ColorFor(JsonTokenKind kind)
        {
            switch (kind)
            {
                case JsonTokenKind.Key: return UiStyle.Accent;
                case JsonTokenKind.Risk: return UiStyle.DangerText;
                case JsonTokenKind.Number: return UiStyle.AccentText;
                case JsonTokenKind.Punct: return UiStyle.TextMuted;
                default: return UiStyle.SuccessText;
            }
        }

        /// <summary>给只读正文框重新上色；调用方负责先写入文本，这里不改动内容。</summary>
        /// <param name="box">只读正文框。</param>
        internal static void Paint(RichTextBox box)
        {
            if (box == null || !box.IsHandleCreated) return;
            var text = box.Text;
            box.Select(0, text.Length);
            box.SelectionColor = UiStyle.Text;
            foreach (var token in Scan(text))
            {
                box.Select(token.Start, token.Length);
                box.SelectionColor = ColorFor(token.Kind);
            }
            box.Select(0, 0);
        }

        private static bool IsBreak(char c)
        {
            return char.IsWhiteSpace(c) || c == ',' || c == '}' || c == ']' || c == ':';
        }
    }
}
