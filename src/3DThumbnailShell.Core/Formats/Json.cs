using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace _3DThumbnailShell.Core.Formats
{
    /// <summary>
    /// 最小 JSON 解析器（tokenizer + 递归下降）。仅解析 glTF 需要的嵌套结构。
    /// 不引入运行时依赖，保证 net48 COM 激活零散布程序集。
    /// </summary>
    internal sealed class JVal
    {
        public Kind KindEnum = Kind.Null;
        public double Num;
        public string Str;
        public bool Bool;
        public List<JVal> Arr;
        public Dictionary<string, JVal> Obj;

        public enum Kind { Null, Num, Str, Bool, Arr, Obj }

        public JVal this[string key] => (Obj != null && Obj.TryGetValue(key, out var v)) ? v : null;
        public JVal this[int i] => Arr != null && i >= 0 && i < Arr.Count ? Arr[i] : null;
        public int Count => Arr?.Count ?? 0;
        public double D => Num;
        public string S => Str;
        public bool Has(string key) => Obj != null && Obj.ContainsKey(key);

        public static JVal NumV(double d) => new JVal { KindEnum = Kind.Num, Num = d };
    }

    internal sealed class JsonParser
    {
        private readonly string _s;
        private int _p;
        public JsonParser(string s) { _s = s; }

        /// <summary>便捷入口（测试用）。</summary>
        internal static JVal ParseJson(string s) => new JsonParser(s).Parse();

        public JVal Parse()
        {
            SkipWs();
            var v = ParseValue();
            return v;
        }

        private JVal ParseValue()
        {
            SkipWs();
            if (_p >= _s.Length) return null;
            var c = _s[_p];
            switch (c)
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"': return new JVal { KindEnum = JVal.Kind.Str, Str = ParseString() };
                case 't': _p += 4; return new JVal { KindEnum = JVal.Kind.Bool, Bool = true };
                case 'f': _p += 5; return new JVal { KindEnum = JVal.Kind.Bool, Bool = false };
                case 'n': _p += 4; return new JVal { KindEnum = JVal.Kind.Null };
                default: return ParseNumber();
            }
        }

        private JVal ParseObject()
        {
            var obj = new JVal { KindEnum = JVal.Kind.Obj, Obj = new Dictionary<string, JVal>() };
            _p++; // {
            SkipWs();
            if (Peek() == '}') { _p++; return obj; }
            while (true)
            {
                SkipWs();
                if (_p >= _s.Length) break;
                var key = ParseString();
                SkipWs();
                if (_p < _s.Length && _s[_p] == ':') _p++;
                var val = ParseValue();
                obj.Obj[key] = val;
                SkipWs();
                if (_p >= _s.Length) break;
                var sep = _s[_p++];
                if (sep == '}') break;
            }
            return obj;
        }

        private JVal ParseArray()
        {
            var arr = new JVal { KindEnum = JVal.Kind.Arr, Arr = new List<JVal>() };
            _p++; // [
            SkipWs();
            if (Peek() == ']') { _p++; return arr; }
            while (true)
            {
                var v = ParseValue();
                if (v != null) arr.Arr.Add(v);
                SkipWs();
                if (_p >= _s.Length) break;
                var sep = _s[_p++];
                if (sep == ']') break;
            }
            return arr;
        }

        private string ParseString()
        {
            if (Peek() == '"') _p++;
            var sb = new StringBuilder();
            while (_p < _s.Length)
            {
                var c = _s[_p++];
                if (c == '"') break;
                if (c == '\\' && _p < _s.Length)
                {
                    var esc = _s[_p++];
                    switch (esc)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (_p + 4 <= _s.Length)
                            {
                                var hex = _s.Substring(_p, 4); _p += 4;
                                sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                break;
                            }
                            sb.Append(esc); break;
                        default: sb.Append(esc); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private JVal ParseNumber()
        {
            var start = _p;
            while (_p < _s.Length)
            {
                var c = _s[_p];
                if (c == ',' || c == ']' || c == '}' || c == ' ' || c == '\n' || c == '\t' || c == '\r') break;
                _p++;
            }
            var t = _s.Substring(start, _p - start);
            var d = double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
            return JVal.NumV(d);
        }

        private void SkipWs()
        {
            while (_p < _s.Length && char.IsWhiteSpace(_s[_p])) _p++;
        }
        private char Peek() => _p < _s.Length ? _s[_p] : '\0';
    }
}