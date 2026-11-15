using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

public sealed class PbStorage
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
        internal int BufferStart;
        internal int BufferLength;
        internal int LengthFieldOffset;
        internal readonly List<ArrayInfo> Arrays = new List<ArrayInfo>();
    }

    private readonly byte[] data;
    private readonly Dictionary<string, Column> columns = new Dictionary<string, Column>(StringComparer.Ordinal);

    public PbStorage(byte[] source)
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
                var column = new Column
                {
                    Kind = kind,
                    ElementSize = elementSize,
                    BufferStart = bufferStart,
                    BufferLength = length,
                    LengthFieldOffset = bufferStart - 4
                };
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

    public bool HasColumn(string table, string name, int kind) =>
        columns.TryGetValue(table + "/" + name, out Column column) && column.Kind == kind;

    public int Count(string table, string name, int kind)
    {
        Column column = GetColumn(table, name, kind);
        return kind == 5 ? column.Arrays.Count : column.Arrays.Count == 0 ? 0 : column.Arrays[0].Count;
    }

    public int Int(string table, string name, int row, int kind = 0)
    {
        Column column = GetColumn(table, name, kind);
        CheckRow(column, row, 4);
        return ReadInt(column.Arrays[0].Offset + 4 * row);
    }

    public float Float(string table, string name, int row)
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

    public int PropertyInt(string key)
    {
        if (!Properties().TryGetValue(key, out string raw) ||
            !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            throw new InvalidDataException("Missing or invalid map property " + key);
        return value;
    }

    public void SetInt(string table, string name, int row, int value)
    {
        Column column = GetColumn(table, name, 0);
        CheckRow(column, row, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, data, column.Arrays[0].Offset + 4 * row, 4);
    }

    public void SetFloat(string table, string name, int row, float value)
    {
        Column column = GetColumn(table, name, 3);
        CheckRow(column, row, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, data, column.Arrays[0].Offset + 4 * row, 4);
    }

    public byte[] Bytes() => (byte[])data.Clone();

    public byte[] RawArray(string table, string name, int kind, int arrayIndex = 0)
    {
        Column column = GetColumn(table, name, kind);
        if (arrayIndex < 0 || arrayIndex >= column.Arrays.Count)
            throw new InvalidDataException("Missing array " + table + "/" + name);
        ArrayInfo array = column.Arrays[arrayIndex];
        var result = new byte[checked(array.Count * column.ElementSize)];
        Buffer.BlockCopy(data, array.Offset, result, 0, result.Length);
        return result;
    }

    // Rebuild one CDataBuf, copying every other column and table byte for byte.
    // Each array keeps its current contents unless a replacement is supplied.
    public PbStorage ReplaceArray(string table, string name, int kind,
                                  int arrayIndex, byte[] replacement)
    {
        Column column = GetColumn(table, name, kind);
        if (arrayIndex < 0 || arrayIndex >= column.Arrays.Count ||
            replacement == null || column.ElementSize < 1 ||
            replacement.Length % column.ElementSize != 0)
            throw new InvalidDataException("Invalid replacement array.");
        var chunks = new byte[column.Arrays.Count][];
        int totalData = 0;
        for (int i = 0; i < chunks.Length; i++)
        {
            ArrayInfo array = column.Arrays[i];
            if (i == arrayIndex) chunks[i] = replacement;
            else
            {
                chunks[i] = new byte[array.Count * column.ElementSize];
                Buffer.BlockCopy(data, array.Offset, chunks[i], 0, chunks[i].Length);
            }
            totalData = checked(totalData + chunks[i].Length);
        }
        int tableOffset = checked(12 + totalData);
        int newLength = checked(tableOffset + chunks.Length * 12);
        var buffer = new byte[newLength];
        WriteInt(buffer, 0, tableOffset);
        WriteInt(buffer, 4, chunks.Length);
        WriteInt(buffer, 8, column.ElementSize);
        int cursor = 12;
        for (int i = 0; i < chunks.Length; i++)
        {
            Buffer.BlockCopy(chunks[i], 0, buffer, cursor, chunks[i].Length);
            int count = chunks[i].Length / column.ElementSize;
            int entry = tableOffset + i * 12;
            WriteInt(buffer, entry, cursor);
            WriteInt(buffer, entry + 4, count);
            WriteInt(buffer, entry + 8, count);
            cursor += chunks[i].Length;
        }
        int oldEnd = column.BufferStart + column.BufferLength;
        var result = new byte[checked(data.Length - column.BufferLength + newLength)];
        Buffer.BlockCopy(data, 0, result, 0, column.BufferStart);
        WriteInt(result, column.LengthFieldOffset, newLength);
        Buffer.BlockCopy(buffer, 0, result, column.BufferStart, newLength);
        Buffer.BlockCopy(data, oldEnd, result, column.BufferStart + newLength,
                         data.Length - oldEnd);
        return new PbStorage(result);
    }

    public PbStorage ReplaceProperty(string key, string value)
    {
        int count = Count("properties", "Name", 5);
        for (int i = 0; i < count; i++)
            if (String("properties", "Name", i) == key)
            {
                ArrayInfo old = GetColumn("properties", "Value", 5).Arrays[i];
                bool terminated = old.Count > 0 &&
                    BitConverter.ToUInt16(data, old.Offset + old.Count * 2 - 2) == 0;
                byte[] encoded = Encoding.Unicode.GetBytes(value + (terminated ? "\0" : ""));
                if (encoded.Length == old.Count * 2)
                {
                    byte[] copy = Bytes();
                    Buffer.BlockCopy(encoded, 0, copy, old.Offset, encoded.Length);
                    return new PbStorage(copy);
                }
                return ReplaceArray("properties", "Value", 5, i, encoded);
            }
        throw new InvalidDataException("Missing map property " + key);
    }

    private static void WriteInt(byte[] target, int offset, int value)
    {
        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, target, offset, 4);
    }

    private static void CheckRow(Column column, int row, int elementSize)
    {
        if (column.Arrays.Count != 1 || column.ElementSize != elementSize ||
            row < 0 || row >= column.Arrays[0].Count)
            throw new IndexOutOfRangeException("Invalid map row.");
    }
}
