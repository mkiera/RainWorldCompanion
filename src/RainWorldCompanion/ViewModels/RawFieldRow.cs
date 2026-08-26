// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RainWorldCompanion.ViewModels;

/// <summary>One field as the file stores it: no label, no validation, no opinion.</summary>
public sealed class RawFieldRow : ObservableObject
{
    private readonly Action<RawFieldRow>? _valueChanged;

    private string _value;

    public RawFieldRow(string key, int occurrence, bool isFlag, string value, Action<RawFieldRow>? valueChanged)
    {
        Key = key;
        Occurrence = occurrence;
        IsFlag = isFlag;
        StoredValue = value;
        _value = value;
        _valueChanged = valueChanged;
    }

    public string Key { get; }

    /// <summary>
    /// A bare token, which the game reads as true by being there at all. Such a row has no value
    /// to edit: it is either written or removed.
    /// </summary>
    public bool IsFlag { get; }

    /// <summary>What the field held when the editor opened.</summary>
    public string StoredValue { get; }

    /// <summary>Which of the fields sharing this key it is, counting from zero.</summary>
    public int Occurrence { get; }

    public string Label => Occurrence == 0
        ? Key
        : Key + " #" + (Occurrence + 1).ToString(CultureInfo.InvariantCulture);

    public string Value
    {
        get => _value;
        set
        {
            if (string.Equals(_value, value, StringComparison.Ordinal))
            {
                return;
            }

            _value = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsChanged));
            OnPropertyChanged(nameof(LengthText));
            _valueChanged?.Invoke(this);
        }
    }

    public bool IsChanged => !string.Equals(_value, StoredValue, StringComparison.Ordinal);

    /// <summary>Several fields hold a packed list hundreds of characters long.</summary>
    public string LengthText => IsFlag
        ? "flag"
        : _value.Length.ToString(CultureInfo.InvariantCulture) + " chars";

    public void Revert() => Value = StoredValue;

    /// <summary>Sets the value without raising the change callback, for a refresh from elsewhere.</summary>
    internal void PullValue(string value)
    {
        if (string.Equals(_value, value, StringComparison.Ordinal))
        {
            return;
        }

        _value = value;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(LengthText));
    }
}
