using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System;

namespace FixFlow.TradeAllocBridge.WPF.ViewModels
{
    public class FixDictionaryViewModel : INotifyPropertyChanged
    {
        private readonly ObservableCollection<FixDictionaryOption> _dictionaryOptions;
        private readonly ObservableCollection<FixMessageOption> _messageSuggestions;
        private readonly ObservableCollection<FixDictionaryFieldOption> _tagSuggestions;
        private readonly ObservableCollection<string> _tagEnumResults;
        private readonly ObservableCollection<string> _tagMessageResults;

        private readonly Dictionary<string, FixDictionaryFieldOption> _fieldByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FixDictionaryFieldOption> _fieldByNumber = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _messageFields = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _messageRequiredFields = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<FixMessageOption>> _fieldMessageUsage = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<FixMessageOption> _messageOptions = new();
        private readonly List<FixDictionaryFieldOption> _allFieldOptions = new();

        private FixDictionaryOption? _selectedDictionary;
        private FixMessageOption? _selectedMessage;
        private FixMessageOption? _selectedMessageSuggestion;
        private FixDictionaryFieldOption? _selectedTagSuggestion;
        private string _messageSearchInput = string.Empty;
        private string _tagSearchInput = string.Empty;
        private string _tagHeader = string.Empty;
        private string _requiredMessageInfo = string.Empty;
        private string _requiredMessageAnswer = string.Empty;
        private bool _isRequiredInMessage;
        private string _statusMessage = "Ready";
        private bool _isMessageSuggestionOpen;
        private bool _isTagSuggestionOpen;
        private bool _suppressMessageSuggestion;
        private bool _suppressTagSuggestion;
        private bool _suppressSuggestionCommit;
        private const string DefaultMessageName = "Allocation";

        public FixDictionaryViewModel()
        {
            _dictionaryOptions = new ObservableCollection<FixDictionaryOption>();
            _messageSuggestions = new ObservableCollection<FixMessageOption>();
            _tagSuggestions = new ObservableCollection<FixDictionaryFieldOption>();
            _tagEnumResults = new ObservableCollection<string>();
            _tagMessageResults = new ObservableCollection<string>();

            LoadDictionaryOptions();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<FixDictionaryOption> DictionaryOptions => _dictionaryOptions;
        public ObservableCollection<FixMessageOption> MessageSuggestions => _messageSuggestions;
        public ObservableCollection<FixDictionaryFieldOption> TagSuggestions => _tagSuggestions;
        public ObservableCollection<string> TagEnumResults => _tagEnumResults;
        public ObservableCollection<string> TagMessageResults => _tagMessageResults;

        public FixDictionaryOption? SelectedDictionary
        {
            get => _selectedDictionary;
            set
            {
                if (_selectedDictionary != value)
                {
                    _selectedDictionary = value;
                    OnPropertyChanged(nameof(SelectedDictionary));
                    LoadSelectedDictionary();
                }
            }
        }

        public string MessageSearchInput
        {
            get => _messageSearchInput;
            set
            {
                if (_messageSearchInput != value)
                {
                    _messageSearchInput = value ?? string.Empty;
                    OnPropertyChanged(nameof(MessageSearchInput));
                    UpdateMessageSuggestions();
                    UpdateSelectedMessageFromInput();
                    UpdateTagSuggestions();
                    UpdateSearchResults();
                }
            }
        }

        public bool IsMessageSuggestionOpen
        {
            get => _isMessageSuggestionOpen;
            set
            {
                if (_isMessageSuggestionOpen != value)
                {
                    _isMessageSuggestionOpen = value;
                    OnPropertyChanged(nameof(IsMessageSuggestionOpen));
                }
            }
        }

        public FixMessageOption? SelectedMessageSuggestion
        {
            get => _selectedMessageSuggestion;
            set
            {
                if (_selectedMessageSuggestion != value)
                {
                    _selectedMessageSuggestion = value;
                    OnPropertyChanged(nameof(SelectedMessageSuggestion));
                    if (_suppressSuggestionCommit || value == null)
                    {
                        return;
                    }

                    ApplyMessageSuggestion(value);
                }
            }
        }

        public FixMessageOption? SelectedMessage
        {
            get => _selectedMessage;
            private set
            {
                if (_selectedMessage != value)
                {
                    _selectedMessage = value;
                    OnPropertyChanged(nameof(SelectedMessage));
                    OnPropertyChanged(nameof(ShowTagMessages));
                }
            }
        }

        public string TagSearchInput
        {
            get => _tagSearchInput;
            set
            {
                if (_tagSearchInput != value)
                {
                    _tagSearchInput = value ?? string.Empty;
                    OnPropertyChanged(nameof(TagSearchInput));
                    UpdateTagSuggestions();
                    UpdateSearchResults();
                }
            }
        }

        public bool IsTagSuggestionOpen
        {
            get => _isTagSuggestionOpen;
            set
            {
                if (_isTagSuggestionOpen != value)
                {
                    _isTagSuggestionOpen = value;
                    OnPropertyChanged(nameof(IsTagSuggestionOpen));
                }
            }
        }

        public FixDictionaryFieldOption? SelectedTagSuggestion
        {
            get => _selectedTagSuggestion;
            set
            {
                if (_selectedTagSuggestion != value)
                {
                    _selectedTagSuggestion = value;
                    OnPropertyChanged(nameof(SelectedTagSuggestion));
                    if (_suppressSuggestionCommit || value == null)
                    {
                        return;
                    }

                    ApplyTagSuggestion(value);
                }
            }
        }

        public string TagHeader
        {
            get => _tagHeader;
            private set
            {
                if (_tagHeader != value)
                {
                    _tagHeader = value;
                    OnPropertyChanged(nameof(TagHeader));
                    OnPropertyChanged(nameof(HasTagHeader));
                }
            }
        }

        public bool HasTagHeader => !string.IsNullOrWhiteSpace(TagHeader);
        public string RequiredMessageInfo
        {
            get => _requiredMessageInfo;
            private set
            {
                if (_requiredMessageInfo != value)
                {
                    _requiredMessageInfo = value;
                    OnPropertyChanged(nameof(RequiredMessageInfo));
                    OnPropertyChanged(nameof(HasRequiredMessageInfo));
                }
            }
        }

        public string RequiredMessageAnswer
        {
            get => _requiredMessageAnswer;
            private set
            {
                if (_requiredMessageAnswer != value)
                {
                    _requiredMessageAnswer = value;
                    OnPropertyChanged(nameof(RequiredMessageAnswer));
                    OnPropertyChanged(nameof(HasRequiredMessageInfo));
                }
            }
        }

        public bool IsRequiredInMessage
        {
            get => _isRequiredInMessage;
            private set
            {
                if (_isRequiredInMessage != value)
                {
                    _isRequiredInMessage = value;
                    OnPropertyChanged(nameof(IsRequiredInMessage));
                }
            }
        }

        public bool HasRequiredMessageInfo =>
            !string.IsNullOrWhiteSpace(RequiredMessageInfo) &&
            !string.IsNullOrWhiteSpace(RequiredMessageAnswer);
        public bool HasTagEnums => _tagEnumResults.Count > 0;
        public bool ShowTagMessages => SelectedMessage == null && _tagMessageResults.Count > 0;

        public string StatusMessage
        {
            get => _statusMessage;
            private set
            {
                if (_statusMessage != value)
                {
                    _statusMessage = value;
                    OnPropertyChanged(nameof(StatusMessage));
                }
            }
        }

        private void LoadDictionaryOptions()
        {
            _dictionaryOptions.Clear();

            var cfgDir = Path.Combine(AppContext.BaseDirectory, "cfg");
            if (!Directory.Exists(cfgDir))
            {
                StatusMessage = "Dictionary folder not found.";
                return;
            }

            var files = Directory.GetFiles(cfgDir, "FIX*.xml")
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                var display = FormatDictionaryDisplay(fileName);
                _dictionaryOptions.Add(new FixDictionaryOption(display, file));
            }

            var defaultOption = _dictionaryOptions
                .FirstOrDefault(opt => string.Equals(Path.GetFileName(opt.Path), "FIX42.xml", StringComparison.OrdinalIgnoreCase));

            SelectedDictionary = defaultOption ?? _dictionaryOptions.FirstOrDefault();
        }

        private void LoadSelectedDictionary()
        {
            ClearDictionaryData();

            if (_selectedDictionary == null || !File.Exists(_selectedDictionary.Path))
            {
                StatusMessage = "Dictionary file not found.";
                return;
            }

            try
            {
                var doc = XDocument.Load(_selectedDictionary.Path);
                var root = doc.Root;
                if (root == null)
                {
                    StatusMessage = "Dictionary file is invalid.";
                    return;
                }

                var fields = root.Element("fields")?.Elements("field") ?? Enumerable.Empty<XElement>();
                foreach (var field in fields)
                {
                    var name = (string?)field.Attribute("name");
                    var number = (string?)field.Attribute("number");
                    var type = (string?)field.Attribute("type");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(number))
                    {
                        continue;
                    }

                    var enums = field.Elements("value")
                        .Select(value => new FixEnumOption(
                            (string?)value.Attribute("enum") ?? string.Empty,
                            (string?)value.Attribute("description") ?? string.Empty))
                        .Where(option => !string.IsNullOrWhiteSpace(option.Value))
                        .ToList();

                    var option = new FixDictionaryFieldOption(name, number, type ?? string.Empty, enums);
                    _allFieldOptions.Add(option);
                    _fieldByName[name] = option;
                    _fieldByNumber[number] = option;
                }

                var messages = root.Element("messages")?.Elements("message") ?? Enumerable.Empty<XElement>();
                foreach (var message in messages)
                {
                    var msgcat = (string?)message.Attribute("msgcat");
                    if (string.Equals(msgcat, "admin", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var name = (string?)message.Attribute("name");
                    var msgType = (string?)message.Attribute("msgtype");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var msgOption = new FixMessageOption(name, msgType ?? string.Empty);
                    _messageOptions.Add(msgOption);

                    var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var requiredFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    CollectFieldNames(message, fieldNames, requiredFields);
                    _messageFields[name] = fieldNames;
                    _messageRequiredFields[name] = requiredFields;

                    foreach (var fieldName in fieldNames)
                    {
                        if (!_fieldMessageUsage.TryGetValue(fieldName, out var list))
                        {
                            list = new List<FixMessageOption>();
                            _fieldMessageUsage[fieldName] = list;
                        }

                        list.Add(msgOption);
                    }
                }

                _messageOptions.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                _allFieldOptions.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                ResetSearchInputs();
                ApplyDefaultMessageSelection();
                StatusMessage = "Dictionary loaded.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to load dictionary: {ex.Message}";
            }
        }

        private void CollectFieldNames(XElement element, HashSet<string> fieldNames, HashSet<string> requiredFields)
        {
            foreach (var child in element.Elements())
            {
                var local = child.Name.LocalName;
                if (string.Equals(local, "field", StringComparison.OrdinalIgnoreCase))
                {
                    var name = (string?)child.Attribute("name");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        fieldNames.Add(name);
                        if (IsRequired(child))
                        {
                            requiredFields.Add(name);
                        }
                    }
                }
                else if (string.Equals(local, "group", StringComparison.OrdinalIgnoreCase))
                {
                    var groupName = (string?)child.Attribute("name");
                    if (!string.IsNullOrWhiteSpace(groupName))
                    {
                        fieldNames.Add(groupName);
                        if (IsRequired(child))
                        {
                            requiredFields.Add(groupName);
                        }
                    }

                    CollectFieldNames(child, fieldNames, requiredFields);
                }
            }
        }

        private void UpdateMessageSuggestions()
        {
            if (_suppressMessageSuggestion)
            {
                return;
            }

            _messageSuggestions.Clear();
            IsMessageSuggestionOpen = false;

            var input = _messageSearchInput.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                return;
            }

            if (!char.IsLetter(input[0]))
            {
                return;
            }

            var matches = input.Length == 1
                ? _messageOptions.Where(option => option.Name.StartsWith(input, StringComparison.OrdinalIgnoreCase)).ToList()
                : _messageOptions.Where(option => option.Name.Contains(input, StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var match in matches.Take(20))
            {
                _messageSuggestions.Add(match);
            }

            IsMessageSuggestionOpen = _messageSuggestions.Count > 0;
        }

        private void UpdateSelectedMessageFromInput()
        {
            if (_suppressMessageSuggestion)
            {
                return;
            }

            var input = _messageSearchInput.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                SelectedMessage = null;
                return;
            }

            var match = _messageOptions.FirstOrDefault(option =>
                string.Equals(option.Name, input, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(option.Display, input, StringComparison.OrdinalIgnoreCase));

            SelectedMessage = match;
            OnPropertyChanged(nameof(ShowTagMessages));
        }

        private void UpdateTagSuggestions()
        {
            if (_suppressTagSuggestion)
            {
                return;
            }

            _tagSuggestions.Clear();
            IsTagSuggestionOpen = false;

            var input = _tagSearchInput.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                return;
            }

            if (input.All(char.IsDigit))
            {
                return;
            }

            if (!char.IsLetter(input[0]))
            {
                return;
            }

            var options = GetCurrentFieldOptions();
            var matches = input.Length == 1
                ? options.Where(option => option.Name.StartsWith(input, StringComparison.OrdinalIgnoreCase)).ToList()
                : options.Where(option => option.Name.Contains(input, StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var match in matches.Take(20))
            {
                _tagSuggestions.Add(match);
            }

            IsTagSuggestionOpen = _tagSuggestions.Count > 0;
        }

        private void UpdateSearchResults()
        {
            TagHeader = string.Empty;
            RequiredMessageInfo = string.Empty;
            RequiredMessageAnswer = string.Empty;
            IsRequiredInMessage = false;
            _tagEnumResults.Clear();
            _tagMessageResults.Clear();
            OnPropertyChanged(nameof(HasTagEnums));
            OnPropertyChanged(nameof(ShowTagMessages));

            var input = _tagSearchInput.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                return;
            }

            var field = FindFieldFromInput(input);
            if (field == null)
            {
                return;
            }

            var requiredInfo = string.Empty;
            var requiredYesNo = string.Empty;
            if (SelectedMessage != null)
            {
                var isRequired = _messageRequiredFields.TryGetValue(SelectedMessage.Name, out var requiredFields) &&
                                 requiredFields.Contains(field.Name);
                IsRequiredInMessage = isRequired;
                requiredInfo = $"Required in {SelectedMessage.Display} message?";
                requiredYesNo = isRequired ? "Yes" : "No";
            }

            TagHeader = $"Name: {field.Name} | Tag: {field.Number} | Type: {field.Type}";
            RequiredMessageInfo = requiredInfo;
            RequiredMessageAnswer = requiredYesNo;

            foreach (var enumOption in field.Enums)
            {
                var description = string.IsNullOrWhiteSpace(enumOption.Description)
                    ? enumOption.Value
                    : $"{enumOption.Value}-{enumOption.Description}";
                _tagEnumResults.Add(description);
            }

            OnPropertyChanged(nameof(HasTagEnums));

            if (SelectedMessage == null && _fieldMessageUsage.TryGetValue(field.Name, out var messages))
            {
                foreach (var message in messages.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                {
                    _tagMessageResults.Add(message.Display);
                }
            }

            OnPropertyChanged(nameof(ShowTagMessages));
        }

        private FixDictionaryFieldOption? FindFieldFromInput(string input)
        {
            var options = GetCurrentFieldOptions();
            if (input.All(char.IsDigit))
            {
                return options.FirstOrDefault(option =>
                    string.Equals(option.Number, input, StringComparison.OrdinalIgnoreCase));
            }

            var exact = options.FirstOrDefault(option =>
                string.Equals(option.Name, input, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact;
            }

            if (input.Length >= 2)
            {
                var matches = options
                    .Where(option => option.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matches.Count == 1)
                {
                    return matches[0];
                }
            }

            return null;
        }

        private IReadOnlyList<FixDictionaryFieldOption> GetCurrentFieldOptions()
        {
            if (SelectedMessage == null)
            {
                return _allFieldOptions;
            }

            if (_messageFields.TryGetValue(SelectedMessage.Name, out var fieldNames))
            {
                var filtered = new List<FixDictionaryFieldOption>();
                foreach (var name in fieldNames)
                {
                    if (_fieldByName.TryGetValue(name, out var option))
                    {
                        filtered.Add(option);
                    }
                }

                filtered.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                return filtered;
            }

            return _allFieldOptions;
        }

        private void ResetSearchInputs()
        {
            _suppressMessageSuggestion = true;
            _suppressTagSuggestion = true;

            MessageSearchInput = string.Empty;
            TagSearchInput = string.Empty;
            SelectedMessage = null;
            SelectedMessageSuggestion = null;
            SelectedTagSuggestion = null;

            _suppressMessageSuggestion = false;
            _suppressTagSuggestion = false;

            _messageSuggestions.Clear();
            _tagSuggestions.Clear();
            IsMessageSuggestionOpen = false;
            IsTagSuggestionOpen = false;

            UpdateSearchResults();
        }

        private void ApplyDefaultMessageSelection()
        {
            var defaultMessage = _messageOptions.FirstOrDefault(option =>
                string.Equals(option.Name, DefaultMessageName, StringComparison.OrdinalIgnoreCase));

            if (defaultMessage == null)
            {
                return;
            }

            _suppressMessageSuggestion = true;
            SelectedMessage = defaultMessage;
            MessageSearchInput = defaultMessage.Display;
            _suppressMessageSuggestion = false;

            UpdateTagSuggestions();
            UpdateSearchResults();
        }

        public void CommitSelectedMessageSuggestion()
        {
            if (SelectedMessageSuggestion == null)
            {
                return;
            }

            ApplyMessageSuggestion(SelectedMessageSuggestion);
        }

        public void CommitSelectedTagSuggestion()
        {
            if (SelectedTagSuggestion == null)
            {
                return;
            }

            ApplyTagSuggestion(SelectedTagSuggestion);
        }

        private void ApplyMessageSuggestion(FixMessageOption option)
        {
            _suppressMessageSuggestion = true;
            SelectedMessage = option;
            MessageSearchInput = option.Display;
            _suppressMessageSuggestion = false;
            IsMessageSuggestionOpen = false;
            _messageSuggestions.Clear();
        }

        private void ApplyTagSuggestion(FixDictionaryFieldOption option)
        {
            _suppressTagSuggestion = true;
            TagSearchInput = option.Name;
            _suppressTagSuggestion = false;
            IsTagSuggestionOpen = false;
            _tagSuggestions.Clear();
        }

        private void ClearDictionaryData()
        {
            _messageOptions.Clear();
            _allFieldOptions.Clear();
            _fieldByName.Clear();
            _fieldByNumber.Clear();
            _messageFields.Clear();
            _messageRequiredFields.Clear();
            _fieldMessageUsage.Clear();

            ResetSearchInputs();
        }

        private static string FormatDictionaryDisplay(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return fileName;

            var baseName = Path.GetFileNameWithoutExtension(fileName) ?? fileName;
            if (!baseName.StartsWith("FIX", StringComparison.OrdinalIgnoreCase))
            {
                return baseName;
            }

            var suffix = baseName.Substring(3);
            if (suffix.Length >= 2 && suffix.All(char.IsDigit))
            {
                var major = suffix[0];
                var minor = suffix.Substring(1);
                return $"FIX.{major}.{minor}";
            }

            return baseName;
        }

        private static bool IsRequired(XElement element)
        {
            var required = (string?)element.Attribute("required");
            return string.Equals(required, "Y", StringComparison.OrdinalIgnoreCase);
        }

        public void SetSuggestionCommitSuppressed(bool suppressed) =>
            _suppressSuggestionCommit = suppressed;

        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public record FixDictionaryOption(string Display, string Path);

    public class FixMessageOption
    {
        public FixMessageOption(string name, string msgType)
        {
            Name = name;
            MsgType = msgType;
        }

        public string Name { get; }
        public string MsgType { get; }
        public string Display => string.IsNullOrWhiteSpace(MsgType) ? Name : $"{Name} ({MsgType})";
    }

    public class FixDictionaryFieldOption
    {
        public FixDictionaryFieldOption(string name, string number, string type, List<FixEnumOption> enums)
        {
            Name = name;
            Number = number;
            Type = type;
            Enums = enums;
        }

        public string Name { get; }
        public string Number { get; }
        public string Type { get; }
        public List<FixEnumOption> Enums { get; }
        public string Display => $"{Name} ({Number})";
    }

    public record FixEnumOption(string Value, string Description);
}
