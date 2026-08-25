// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RainWorldSaveManager.ViewModels;

/// <summary>
/// One field of a campaign record, shown as the file stores it.
///
/// The boxes above this in the panel are a curated few fields with names a person recognises. A
/// save holds many more, and a save a mod has written holds fields this app has never heard of.
/// Those are the ones this row is for: no label, no validation, no opinion about what the value
/// should look like, just the key and the characters beside it.
/// </summary>
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
    /// True when the field is a bare token, which the game reads as true by being there at all.
    /// A row like this has no value to edit: it is either written or it is removed.
    /// </summary>
    public bool IsFlag { get; }

    /// <summary>What the field held when the editor opened, for the revert button and its tooltip.</summary>
    public string StoredValue { get; }

    /// <summary>Which of the fields sharing this key it is, counting from zero.</summary>
    public int Occurrence { get; }

    /// <summary>The key, with a number after it when the record carries this key more than once.</summary>
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

    /// <summary>
    /// How long the value is. Several fields hold a packed list hundreds of characters long, and a
    /// box showing the first forty of them gives no sense of that.
    /// </summary>
    public string LengthText => IsFlag
        ? "flag"
        : _value.Length.ToString(CultureInfo.InvariantCulture) + " chars";

    /// <summary>Puts the value back to what the file held when the editor opened.</summary>
    public void Revert() => Value = StoredValue;

    /// <summary>Sets the value without telling anyone, for a refresh driven by an edit made elsewhere.</summary>
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
