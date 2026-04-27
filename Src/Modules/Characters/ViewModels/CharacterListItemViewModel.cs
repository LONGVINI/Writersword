using System;
using System.Reactive;
using ReactiveUI;
using Writersword.Src.Modules.Characters.Resources;

namespace Writersword.Modules.Characters.ViewModels
{
    public class CharacterListItemViewModel : ReactiveObject
    {
        private bool _isBeingNamed;
        private string _inlineName = string.Empty;
        private bool _isRenaming;
        private string _pendingRename = string.Empty;
        private bool _isSelected;
        private string _name;
        private string _color;

        public string Id { get; }

        public string Name
        {
            get => _name;
            private set => this.RaiseAndSetIfChanged(ref _name, value);
        }

        public string ShortDescription { get; }

        public string Color
        {
            get => _color;
            set
            {
                this.RaiseAndSetIfChanged(ref _color, value);
                OnColorChanged?.Invoke(Id, value);
            }
        }

        public string FallbackIcon { get; }
        public bool IsCollective { get; }
        public int RelationshipsCount { get; }
        public bool IsNewlyCreated { get; }

        public bool IsBeingNamed
        {
            get => _isBeingNamed;
            set
            {
                this.RaiseAndSetIfChanged(ref _isBeingNamed, value);
                this.RaisePropertyChanged(nameof(IsShowingNameDisplay));
            }
        }

        public string InlineName
        {
            get => _inlineName;
            set => this.RaiseAndSetIfChanged(ref _inlineName, value);
        }

        public bool IsRenaming
        {
            get => _isRenaming;
            set
            {
                this.RaiseAndSetIfChanged(ref _isRenaming, value);
                this.RaisePropertyChanged(nameof(IsShowingNameDisplay));
            }
        }

        public string PendingRename
        {
            get => _pendingRename;
            set => this.RaiseAndSetIfChanged(ref _pendingRename, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        // true когда не в режиме ввода/переименовани€ Ч показывает нормальное отображение
        public bool IsShowingNameDisplay => !_isBeingNamed && !_isRenaming;

        // колбэки, устанавливаютс€ родительским ViewModel
        public Action<string, string>? OnConfirmName { get; set; }    // (id, newName)
        public Action<string>? OnCancelNewCharacter { get; set; }     // (id) Ч отмена нового персонажа = удаление
        public Action<string>? OnDeleteRequested { get; set; }        // (id)
        public Action<string, string>? OnColorChanged { get; set; }   // (id, newColor)

        // команды Ч выполн€ютс€ из AXAML напр€мую через {Binding}
        public ReactiveCommand<Unit, Unit> ConfirmNameCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelNameCommand { get; }
        public ReactiveCommand<Unit, Unit> StartRenameCommand { get; }
        public ReactiveCommand<Unit, Unit> ConfirmRenameCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelRenameCommand { get; }
        public ReactiveCommand<Unit, Unit> RequestDeleteCommand { get; }

        public CharacterListItemViewModel(
            Models.Character character,
            int relationshipsCount = 0,
            bool isNewlyCreated = false)
        {
            Id = character.Id;
            _name = character.Name;
            ShortDescription = character.ShortDescription;
            _color = character.Color;
            FallbackIcon = character.FallbackIcon;
            IsCollective = character.IsCollective;
            RelationshipsCount = relationshipsCount;
            IsNewlyCreated = isNewlyCreated;

            _isBeingNamed = isNewlyCreated;
            _inlineName = isNewlyCreated ? string.Empty : character.Name;

            ConfirmNameCommand = ReactiveCommand.Create(() =>
            {
                var resolved = string.IsNullOrWhiteSpace(InlineName)
                    ? CharactersStrings.Character_DefaultName
                    : InlineName.Trim();
                Name = resolved;
                IsBeingNamed = false;
                OnConfirmName?.Invoke(Id, resolved);
            });

            CancelNameCommand = ReactiveCommand.Create(() =>
            {
                IsBeingNamed = false;
                OnCancelNewCharacter?.Invoke(Id);
            });

            StartRenameCommand = ReactiveCommand.Create(() =>
            {
                PendingRename = Name;
                IsRenaming = true;
            });

            ConfirmRenameCommand = ReactiveCommand.Create(() =>
            {
                var resolved = string.IsNullOrWhiteSpace(PendingRename) ? Name : PendingRename.Trim();
                Name = resolved;
                IsRenaming = false;
                OnConfirmName?.Invoke(Id, resolved);
            });

            CancelRenameCommand = ReactiveCommand.Create(() =>
            {
                IsRenaming = false;
            });

            RequestDeleteCommand = ReactiveCommand.Create(() =>
            {
                OnDeleteRequested?.Invoke(Id);
            });
        }
    }
}