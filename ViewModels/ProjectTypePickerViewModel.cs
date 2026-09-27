using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using Writersword.ProjectTypes.Common;

namespace Writersword.ViewModels
{
    /// <summary>
    /// Окно выбора типа проекта для документа Word. Список тот же, что и на экране
    /// приветствия: тот же реестр, те же значки и подписи, и отмечен сначала тот
    /// тип, что уже отмечен там.
    /// </summary>
    public class ProjectTypePickerViewModel : ViewModelBase
    {
        private string _selectedProjectType;

        /// <summary>Имя документа, из которого делается проект.</summary>
        public string DocumentName { get; }

        /// <summary>Пояснение под заголовком: какой документ и что с ним будет.</summary>
        public string Subtitle =>
            $"Документ «{DocumentName}» станет новым проектом рядом с исходным файлом. Сам документ останется как есть.";

        /// <summary>Типы проектов.</summary>
        public ObservableCollection<ProjectTypeItem> ProjectTypes { get; } = new();

        /// <summary>Выбранный тип.</summary>
        public string SelectedProjectType
        {
            get => _selectedProjectType;
            set => this.RaiseAndSetIfChanged(ref _selectedProjectType, value);
        }

        public ProjectTypePickerViewModel(string documentName, string preselectedProjectType)
        {
            DocumentName = documentName;
            _selectedProjectType = preselectedProjectType;

            var registry = App.Services.GetRequiredService<ProjectTypeRegistry>();

            foreach (var type in registry.GetAll())
            {
                var item = new ProjectTypeItem
                {
                    Id = type.Id,
                    DisplayName = type.DisplayName,
                    Icon = type.Icon,
                    IsSelected = type.Id == _selectedProjectType
                };

                item.WhenAnyValue(x => x.IsSelected)
                    .Subscribe(selected =>
                    {
                        if (selected)
                            SelectedProjectType = item.Id;
                    });

                ProjectTypes.Add(item);
            }

            // Отмеченного на экране типа в реестре нет — отмечается первый, иначе
            // кнопке «Создать» нечего было бы вернуть.
            bool anySelected = false;
            foreach (var item in ProjectTypes)
                anySelected |= item.IsSelected;

            if (!anySelected && ProjectTypes.Count > 0)
                ProjectTypes[0].IsSelected = true;
        }
    }
}
