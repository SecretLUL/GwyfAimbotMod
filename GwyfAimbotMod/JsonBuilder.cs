using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace GwyfAimbotMod
{
    /// <summary>
    /// Minimaler JSON-Schreiber. Bewusst handgeschrieben: erzwingt InvariantCulture
    /// (das Spiel laeuft auch unter Locales mit Dezimalkomma) und rundreisefaehige
    /// Float-Ausgabe, und macht nicht-endliche Werte als Zeichenkette sichtbar,
    /// statt ungueltiges JSON zu erzeugen.
    /// </summary>
    internal sealed class JsonBuilder
    {
        private readonly StringBuilder _sb = new StringBuilder(16384);
        private readonly Stack<bool> _parents = new Stack<bool>();
        private int _depth;
        private bool _first = true;

        public void BeginObject(string name) { Open(name, '{'); }
        public void EndObject() { Close('}'); }
        public void BeginArray(string name) { Open(name, '['); }
        public void EndArray() { Close(']'); }

        public void Prop(string name, string value)
        {
            Pre(name);
            if (value == null) _sb.Append("null");
            else AppendString(value);
        }

        public void Prop(string name, bool value)
        {
            Pre(name);
            _sb.Append(value ? "true" : "false");
        }

        public void Prop(string name, int value)
        {
            Pre(name);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        public void Prop(string name, float value)
        {
            Pre(name);
            AppendFloat(value);
        }

        public void PropNullable(string name, float? value)
        {
            Pre(name);
            if (value.HasValue) AppendFloat(value.Value);
            else _sb.Append("null");
        }

        public void Prop(string name, Vector3 v)
        {
            Pre(name);
            _sb.Append("{ \"x\": "); AppendFloat(v.x);
            _sb.Append(", \"y\": "); AppendFloat(v.y);
            _sb.Append(", \"z\": "); AppendFloat(v.z);
            _sb.Append(" }");
        }

        public void Prop(string name, Quaternion q)
        {
            Pre(name);
            _sb.Append("{ \"x\": "); AppendFloat(q.x);
            _sb.Append(", \"y\": "); AppendFloat(q.y);
            _sb.Append(", \"z\": "); AppendFloat(q.z);
            _sb.Append(", \"w\": "); AppendFloat(q.w);
            _sb.Append(" }");
        }

        public void PropEnum(string name, string text, int value)
        {
            Pre(name);
            _sb.Append("{ \"name\": ");
            AppendString(text);
            _sb.Append(", \"value\": ").Append(value.ToString(CultureInfo.InvariantCulture)).Append(" }");
        }

        public override string ToString()
        {
            return _sb.ToString();
        }

        private void Open(string name, char brace)
        {
            Pre(name);
            _sb.Append(brace);
            _parents.Push(false);
            _first = true;
            _depth++;
        }

        private void Close(char brace)
        {
            _depth--;
            if (!_first)
            {
                _sb.Append('\n');
                _sb.Append(' ', _depth * 2);
            }
            _sb.Append(brace);
            _first = _parents.Count > 0 ? _parents.Pop() : false;
        }

        private void Pre(string name)
        {
            if (!_first) _sb.Append(',');
            if (_depth > 0)
            {
                _sb.Append('\n');
                _sb.Append(' ', _depth * 2);
            }
            _first = false;

            if (name != null)
            {
                AppendString(name);
                _sb.Append(": ");
            }
        }

        private void AppendFloat(float value)
        {
            if (float.IsNaN(value)) { _sb.Append("\"NaN\""); return; }
            if (float.IsPositiveInfinity(value)) { _sb.Append("\"Infinity\""); return; }
            if (float.IsNegativeInfinity(value)) { _sb.Append("\"-Infinity\""); return; }
            _sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private void AppendString(string s)
        {
            _sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"':  _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\b': _sb.Append("\\b"); break;
                    case '\f': _sb.Append("\\f"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < ' ') _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }
    }
}
