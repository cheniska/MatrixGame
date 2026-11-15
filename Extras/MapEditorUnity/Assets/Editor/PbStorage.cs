using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

internal sealed class PbStorage
{
    internal sealed class ArrayInfo
    {
        internal int Offset;
        internal int Count;
    }

    internal sealed class Column
    {
        internal int Kind;
        internal int ElementSize;
        internal readonly List<ArrayInfo> Arrays = new List<ArrayInfo>();
    }

    private readonly byte[] data;
    private readonly Dictionary<string, Column> columns = new Dictionary<string, Column>(StringComparer.Ordinal);

    internal PbStorage(byte[] source)
    {
        data = (byte[])source.Clone();
        if (data.Length < 12 || Encoding.ASCII.GetString(data, 0, 4) != "STRG" || ReadInt(4) != 0)
            throw new InvalidDataException("Expected uncompressed STRG v0 map.");
        int position = 8;
        int tableCount = ReadInt(position);
        position += 4;
        if (tableCount < 0 || tableCount > 10000)
            throw new InvalidDataException("Invalid table count.");
        for (int table = 0; table < tableCount; table++)
        {
            string tableName = ReadWide(ref position);
            int columnCount = ReadInt(position);
            position += 4;
            if (columnCount < 0 || columnCount > 1000)
                throw new InvalidDataException("Invalid column count.");
            for (int item = 0; item < columnCount; item++)
            {
                string columnName = ReadWide(ref position);
                int kind = ReadInt(position);
                int length = ReadInt(position + 4);
                position += 8;
                if (kind < 0 || kind > 5 || length < 12 || (long)position + length > data.Length)
                    throw new InvalidDataException("Invalid storage column.");
                int bufferStart = position;
                int tableOffset = ReadInt(bufferStart);
                int arrayCount = ReadInt(bufferStart + 4);
                int elementSize = ReadInt(bufferStart + 8);
                if (arrayCount < 0 || arrayCount > 1000000 || tableOffset < 12 ||
                    (long)tableOffset + 12L * arrayCount > length)
                    throw new InvalidDataException("Invalid CDataBuf header.");
                var column = new Column { Kind = kind, ElementSize = elementSize };
                for (int index = 0; index < arrayCount; index++)
                {
                    int entry = bufferStart + tableOffset + 12 * index;
                    int offset = ReadInt(entry);
                    int count = ReadInt(entry + 4);
                    int capacity = ReadInt(entry + 8);
                    if (offset < 12 || count < 0 || capacity < count ||
                        (long)offset + (long)count * elementSize > length)
                        throw new InvalidDataException("Invalid CDataBuf array.");
                    column.Arrays.Add(new ArrayInfo { Offset = bufferStart + offset, Count = count });
                }
                columns.Add(tableName + "/" + columnName, column);
                position += length;
            }
        }
        if (position != data.Length)
            throw new InvalidDataException("Trailing map bytes.");
    }

    private int ReadInt(int offset)
    {
        if (offset < 0 || offset + 4 > data.Length)
            throw new InvalidDataException("Truncated map.");
        return BitConverter.ToInt32(data, offset);
    }

    private string ReadWide(ref int position)
    {
        int start = position;
        while (position + 1 < data.Length && BitConverter.ToUInt16(data, position) != 0)
            position += 2;
        if (position + 1 >= data.Length)
            throw new InvalidDataException("Unterminated map string.");
        string text = Encoding.Unicode.GetString(data, start, position - start);
        position += 2;
        return text;
    }

    internal Column GetColumn(string table, string name, int kind)
    {
        if (!columns.TryGetValue(table + "/" + name, out Column column) || column.Kind != kind)
            throw new InvalidDataException("Missing or invalid " + table + "/" + name);
        return column;
    }

    internal int Count(string table, string name, int kind)
    {
        Column column = GetColumn(table, name, kind);
        return kind == 5 ? column.Arrays.Count : column.Arrays.Count == 0 ? 0 : column.Arrays[0].Count;
    }

    internal int Int(string table, string name, int row, int kind = 0)
    {
        Column column = GetColumn(table, name, kind);
        CheckRow(column, row, 4);
        return ReadInt(column.Arrays[0].Offset + 4 * row);
    }

    internal float Float(string table, string name, int row)
    {
        Column column = GetColumn(table, name, 3);
        CheckRow(column, row, 4);
        return BitConverter.ToSingle(data, column.Arrays[0].Offset + 4 * row);
    }

    internal string String(string table, string name, int row)
    {
        Column column = GetColumn(table, name, 5);
        if (row < 0 || row >= column.Arrays.Count)
            throw new IndexOutOfRangeException(table + "/" + name);
        ArrayInfo array = column.Arrays[row];
        return Encoding.Unicode.GetString(data, array.Offset, 2 * array.Count).TrimEnd('\0');
    }

    internal Dictionary<string, string> Properties()
    {
        int count = Count("properties", "Name", 5);
        if (count != Count("properties", "Value", 5))
            throw new InvalidDataException("Property columns have different lengths.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
            result[String("properties", "Name", i)] = String("properties", "Value", i);
        return result;
    }

    internal int PropertyInt(string key)
    {
        if (!Properties().TryGetValue(key, out string raw) ||
            !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            throw new InvalidDataException("Missing or invalid map property " + key);
        return value;
    }

    internal void SetInt(string table, string name, int row, int value)
    {
        Column column = GetColumn(table, name, 0);
        CheckRow(column, row, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, data, column.Arrays[0].Offset + 4 * row, 4);
    }

    internal void SetFloat(string table, string name, int row, float value)
    {
        Column column = GetColumn(table, name, 3);
        CheckRow(column, row, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, data, column.Arrays[0].Offset + 4 * row, 4);
    }

    internal byte[] Bytes() => (byte[])data.Clone();

    private static void CheckRow(Column column, int row, int elementSize)
    {
        if (column.Arrays.Count != 1 || column.ElementSize != elementSize ||
            row < 0 || row >= column.Arrays[0].Count)
            throw new IndexOutOfRangeException("Invalid map row.");
    }
}
