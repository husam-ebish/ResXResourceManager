﻿namespace ResXManager.VSIX.Visuals
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.ComponentModel.DataAnnotations;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Text;

    using EnvDTE;

    using JetBrains.Annotations;

    using PropertyChanged;

    using Throttle;

    using ResXManager.Model;
    using ResXManager.VSIX.Properties;

    using TomsToolbox.Essentials;
    using TomsToolbox.Wpf;

    internal sealed class MoveToResourceViewModel : INotifyPropertyChanged, IDataErrorInfo
    {
        [NotNull]
        private readonly string _extension;

        public MoveToResourceViewModel([NotNull, ItemNotNull] ICollection<string> patterns, [NotNull][ItemNotNull] ICollection<ResourceEntity> resourceEntities, [NotNull] string text, [NotNull] string extension, [CanBeNull] string className, [CanBeNull] string functionName, [CanBeNull] string fileName)
        {
            ResourceEntities = resourceEntities;
            SelectedResourceEntity = resourceEntities.FirstOrDefault();

            ExistingEntries = resourceEntities
                .SelectMany(entity => entity.Entries)
                .Where(entry => entry.Values[null] == text)
                .ToArray();
            ReuseExisiting = ExistingEntries.Any();

            SelectedResourceEntry = ExistingEntries.FirstOrDefault();
            _extension = extension;

            Replacements = patterns.Select(p => new Replacement(p, EvaluatePattern)).ToArray();
            Keys = new[] { CreateKey(text, null, null), CreateKey(text, null, functionName), CreateKey(text, className ?? fileName, functionName) }.Distinct().ToArray();
            Key = Keys.Skip(SelectedKeyIndex).FirstOrDefault() ?? Keys.FirstOrDefault();
            Value = text;
        }

        [NotNull]
        [ItemNotNull]
        public ICollection<ResourceEntity> ResourceEntities { get; }

        [Required]
        [CanBeNull]
        public ResourceEntity SelectedResourceEntity { get; set; }

        [CanBeNull]
        public ResourceTableEntry SelectedResourceEntry { get; set; }

        [NotNull, ItemNotNull]
        public ICollection<string> Keys { get; }

        [Required(AllowEmptyStrings = false)]
        [DependsOn(nameof(ReuseExisiting), nameof(SelectedResourceEntity))] // must raise a change event for key, key validation is different when these change
        [CanBeNull]
        public string Key { get; set; }

        [NotNull, ItemNotNull]
        public ICollection<Replacement> Replacements { get; }

        [CanBeNull]
        [Required]
        public Replacement Replacement { get; set; }

        [CanBeNull]
        [Required(AllowEmptyStrings = false)]
        public string Value { get; set; }

        [CanBeNull]
        public string Comment { get; set; }

        public bool ReuseExisiting { get; set; }

        [NotNull]
        [ItemNotNull]
        public ICollection<ResourceTableEntry> ExistingEntries { get; }

        public int SelectedReplacementIndex
        {
            get => Settings.Default.MoveToResourcePreferedReplacementPatternIndex[_extension];
            set => Settings.Default.MoveToResourcePreferedReplacementPatternIndex[_extension] = value;
        }

        public int SelectedKeyIndex
        {
            get => Settings.Default.MoveToResourcePreferedKeyPatternIndex[_extension];
            set
            {
                if (value >= 0) 
                    Settings.Default.MoveToResourcePreferedKeyPatternIndex[_extension] = value;
            }
        }

        [CanBeNull]
        private string GetKeyErrors([CanBeNull] string propertyName)
        {
            if (ReuseExisiting)
                return null;

            if (!string.Equals(propertyName, nameof(Key), StringComparison.Ordinal))
                return null;

            var key = Key;

            if (string.IsNullOrEmpty(key))
                return null;

            if (!key.All(c => (c == '_') || char.IsLetterOrDigit(c)) || char.IsDigit(key.FirstOrDefault()))
                return Resources.KeyContainsInvalidCharacters;

            if (KeyExists(key))
                return Resources.DuplicateKey;

            return null;
        }

        private bool KeyExists([CanBeNull] string value)
        {
            return SelectedResourceEntity?.Entries.Any(entry => string.Equals(entry.Key, value, StringComparison.OrdinalIgnoreCase)) ?? false;
        }

        [NotNull]
        private static string GetLocalNamespace([CanBeNull] ProjectItem resxItem)
        {
            try
            {
                var resxPath = resxItem?.TryGetFileName();
                if (resxPath == null)
                    return string.Empty;

                var resxFolder = Path.GetDirectoryName(resxPath);
                var project = resxItem.ContainingProject;
                var projectFolder = Path.GetDirectoryName(project?.FullName);
                var rootNamespace = project?.Properties?.Item(@"RootNamespace")?.Value?.ToString() ?? string.Empty;

                if ((resxFolder == null) || (projectFolder == null))
                    return string.Empty;

                var localNamespace = rootNamespace;
                if (resxFolder.StartsWith(projectFolder, StringComparison.OrdinalIgnoreCase))
                {
                    localNamespace += resxFolder.Substring(projectFolder.Length)
                        .Replace('\\', '.')
                        .Replace("My Project", "My"); // VB workaround
                }

                return localNamespace;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        [Throttled(typeof(DispatcherThrottle))]
        private void Update()
        {
            Replacements.ForEach(r => r.Update());
        }

        [NotNull]
        private string EvaluatePattern([NotNull] string pattern)
        {
            var entity = ReuseExisiting ? SelectedResourceEntry?.Container : SelectedResourceEntity;
            var key = ReuseExisiting ? SelectedResourceEntry?.Key : Key;
            var localNamespace = GetLocalNamespace((entity?.NeutralProjectFile as DteProjectFile)?.DefaultProjectItem);

            return pattern.Replace(@"$File", entity?.BaseName)
                .Replace(@"$Key", key)
                .Replace(@"$Namespace", localNamespace);
        }

        [NotNull]
        private static string CreateKey([NotNull] string text, [CanBeNull] string className, [CanBeNull] string functionName)
        {
            var keyBuilder = new StringBuilder();

            if (!string.IsNullOrEmpty(className))
                keyBuilder.Append(className).Append(@"_");
            if (!string.IsNullOrEmpty(functionName))
                keyBuilder.Append(functionName).Append(@"_");

            var makeUpper = true;

            foreach (var c in text)
            {
                if (!IsCharValidForSymbol(c))
                {
                    makeUpper = true;
                }
                else
                {
                    keyBuilder.Append(makeUpper ? char.ToUpper(c, CultureInfo.CurrentCulture) : c);
                    makeUpper = false;
                }
            }

            var key = keyBuilder.ToString();

            if (!IsCharValidForSymbolStart(key.FirstOrDefault()))
                key = @"_" + key;

            return key;
        }

        private static bool IsCharValidForSymbol(char c)
        {
            return (c == '_') || char.IsLetterOrDigit(c);
        }

        private static bool IsCharValidForSymbolStart(char c)
        {
            return (c == '_') || char.IsLetter(c);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator, UsedImplicitly]
        private void OnPropertyChanged([CallerMemberName][CanBeNull] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            Update();
        }

        [CanBeNull]
        string IDataErrorInfo.this[[CanBeNull] string columnName] => GetKeyErrors(columnName);

        [CanBeNull]
        string IDataErrorInfo.Error => null;
    }

    public sealed class Replacement : INotifyPropertyChanged
    {
        [NotNull]
        private readonly string _pattern;
        [NotNull]
        private readonly Func<string, string> _evaluator;

        public Replacement([NotNull] string pattern, [NotNull] Func<string, string> evaluator)
        {
            _pattern = pattern;
            _evaluator = evaluator;
        }

        [CanBeNull]
        public string Value => _evaluator(_pattern);

        public event PropertyChangedEventHandler PropertyChanged;

        public void Update()
        {
            OnPropertyChanged(nameof(Value));
        }

        private void OnPropertyChanged([NotNull] string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
