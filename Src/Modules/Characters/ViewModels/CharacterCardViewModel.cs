using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Reactive;
using System.Reactive.Linq;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.ViewModels.Tabs;

namespace Writersword.Modules.Characters.ViewModels
{
    public class CharacterCardViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterCardViewModel>();

        /// <summary>
        /// Число видимых вкладок карточки: Основное, Параметры, Связи.
        /// Используется горячими клавишами перебора вкладок.
        /// </summary>
        public const int TabCount = 3;

        public string CharacterId { get; }

        public CharacterBasicsTabViewModel BasicsTab { get; }
        public CharacterParametersTabViewModel ParametersTab { get; }
        public CharacterRelationshipsTabViewModel RelationshipsTab { get; }
        public CharacterContextsTabViewModel ContextsTab { get; }
        public CharacterNotesTabViewModel NotesTab { get; }
        public CharacterPersonalTimelineTabViewModel PersonalTimelineTab { get; }
        public CharacterHistoryTabViewModel HistoryTab { get; }

        private int _selectedTabIndex = 0;
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
        }

        public string DisplayName => BasicsTab.Name;
        public string Color => BasicsTab.Color;
        public bool IsCollective => BasicsTab.IsCollective;

        public ReactiveCommand<Unit, Unit> SaveCommand { get; }

        // Поднимается после каждого сохранения персонажа в сервис — по нему
        // вьюмодель модуля обновляет строки бокового списка редактора
        // (имя, описание, иконку, цвет) без пересборки списка.
        public event Action<string>? Saved;

        private readonly ICharacterService _characterService;
        private readonly IDisposable _autoSaveSubscription;

        public CharacterCardViewModel(
            ICharacterService characterService,
            IRelationshipService relationshipService,
            ICharacterAnketaService anketaService,
            Character character,
            ICharacterAvatarService? avatarService = null,
            IEnumerable<CharacterFolder>? folders = null)
        {
            _characterService = characterService;
            CharacterId = character.Id;

            BasicsTab = new CharacterBasicsTabViewModel(characterService, character, avatarService, anketaService);
            ParametersTab = new CharacterParametersTabViewModel(characterService, anketaService, character);
            RelationshipsTab = new CharacterRelationshipsTabViewModel(relationshipService, characterService, character.Id);
            ContextsTab = new CharacterContextsTabViewModel(character);
            NotesTab = new CharacterNotesTabViewModel(character);
            PersonalTimelineTab = new CharacterPersonalTimelineTabViewModel(character);
            HistoryTab = new CharacterHistoryTabViewModel(character);

            SaveCommand = ReactiveCommand.Create(Save);

            // Enter в поле имени сохраняет сразу, без ожидания Throttle автосейва.
            BasicsTab.ImmediateSaveRequested += Save;

            // Групповые обращения ссылаются на папки проекта: список папок
            // приходит снаружи, потому что карточка о них не знает.
            BasicsTab.SetAddressFolders(folders ?? System.Linq.Enumerable.Empty<CharacterFolder>());

            // Состав карточки задаётся в «Общем» — это свойство ядра. Поля
            // подключённого набора живут на вкладке параметров, поэтому её
            // содержимое пересобирается вслед за изменением состава.
            BasicsTab.AnketasChanged += () =>
            {
                var updated = _characterService.GetById(CharacterId);
                if (updated != null) ParametersTab.ReloadFromModel(updated);
            };

            // Автосохранение карточки: кнопки Save в шапке больше нет, правки
            // вкладки Basics применяются к персонажу сами — с задержкой после
            // последнего изменения, чтобы не дёргать сервис на каждый символ.
            // Коллекции (алиасы, теги, статусы) не поднимают Changed у
            // ReactiveObject, поэтому подписываются отдельно и сливаются
            // в общий поток.
            var propertyChanges = BasicsTab.Changed.Select(_ => Unit.Default);
            var collectionChanges = Observable.Merge(
                FromCollection(BasicsTab.AlternateNames),
                FromCollection(BasicsTab.Tags),
                FromCollection(BasicsTab.Labels));

            // Правки параметров тоже должны сохраняться. До этого Save применял
            // только вкладку Basics, а GetParameters не вызывался нигде — всё,
            // что вводилось в параметрах, терялось при перезагрузке проекта.
            var parameterChanges = Observable.FromEvent(
                h => ParametersTab.Edited += h,
                h => ParametersTab.Edited -= h);

            // Throttle отрабатывает на таймере пула потоков — сохранение
            // переносится на UI-поток через диспетчер Avalonia.
            _autoSaveSubscription = propertyChanges
                .Merge(collectionChanges)
                .Merge(parameterChanges.Select(_ => Unit.Default))
                .Throttle(TimeSpan.FromMilliseconds(600))
                .Subscribe(_ => Avalonia.Threading.Dispatcher.UIThread.Post(Save));
        }

        private static IObservable<Unit> FromCollection(INotifyCollectionChanged collection)
            => Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                    h => collection.CollectionChanged += h,
                    h => collection.CollectionChanged -= h)
                .Select(_ => Unit.Default);

        private void Save()
        {
            var character = _characterService.GetById(CharacterId);
            if (character == null) return;
            BasicsTab.ApplyTo(character);
            character.Parameters = ParametersTab.GetParameters();
            _characterService.Update(character);
            _logger.Debug("Character {Id} saved", CharacterId);
            Saved?.Invoke(CharacterId);
        }
    }
}