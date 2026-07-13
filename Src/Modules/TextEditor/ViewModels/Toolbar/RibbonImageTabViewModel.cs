using System.Windows.Input;
using ReactiveUI;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// ViewModel контекстной вкладки «Формат» (работа с выделенной картинкой).
    /// Появляется только когда на канвасе выделено изображение.
    /// </summary>
    public sealed class RibbonImageTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        private TextAlignment _currentAlignment = TextAlignment.Left;
        private WrapMode _currentWrap = WrapMode.Inline;
        private bool _isAspectLocked = true;

        /// <summary>Текущее выравнивание картинки в колонке (для подсветки кнопок).</summary>
        public TextAlignment CurrentAlignment
        {
            get => _currentAlignment;
            private set => this.RaiseAndSetIfChanged(ref _currentAlignment, value);
        }

        /// <summary>Текущий режим обтекания (для подсветки кнопок).</summary>
        public WrapMode CurrentWrap
        {
            get => _currentWrap;
            private set => this.RaiseAndSetIfChanged(ref _currentWrap, value);
        }

        /// <summary>Заблокированы ли пропорции при изменении размера.</summary>
        public bool IsAspectLocked
        {
            get => _isAspectLocked;
            private set => this.RaiseAndSetIfChanged(ref _isAspectLocked, value);
        }

        // ── Выравнивание картинки в колонке ───────────────────────────────
        public ICommand AlignLeftCommand { get; }
        public ICommand AlignCenterCommand { get; }
        public ICommand AlignRightCommand { get; }

        // ── Обтекание текстом ─────────────────────────────────────────────
        public ICommand WrapInlineCommand { get; }
        public ICommand WrapSquareCommand { get; }
        public ICommand WrapBehindCommand { get; }

        // ── Прочее ────────────────────────────────────────────────────────
        public ICommand ToggleAspectCommand { get; }
        public ICommand DeleteImageCommand { get; }

        public RibbonImageTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target ?? throw new System.ArgumentNullException(nameof(target));

            AlignLeftCommand = ReactiveCommand.Create(() =>
                { _target.SetAlignment(TextAlignment.Left); SyncFromTarget(); });
            AlignCenterCommand = ReactiveCommand.Create(() =>
                { _target.SetAlignment(TextAlignment.Center); SyncFromTarget(); });
            AlignRightCommand = ReactiveCommand.Create(() =>
                { _target.SetAlignment(TextAlignment.Right); SyncFromTarget(); });

            WrapInlineCommand = ReactiveCommand.Create(() =>
                { _target.SetImageWrapMode(WrapMode.Inline); SyncFromTarget(); });
            WrapSquareCommand = ReactiveCommand.Create(() =>
                { _target.SetImageWrapMode(WrapMode.Square); SyncFromTarget(); });
            WrapBehindCommand = ReactiveCommand.Create(() =>
                { _target.SetImageWrapMode(WrapMode.Behind); SyncFromTarget(); });

            ToggleAspectCommand = ReactiveCommand.Create(() =>
                { _target.SetImageLockAspect(!IsAspectLocked); SyncFromTarget(); });
            DeleteImageCommand = ReactiveCommand.Create(() => _target.DeleteSelectedImage());
        }

        /// <summary>
        /// Читает параметры выделенной картинки из target и обновляет состояние вкладки.
        /// Вызывается при выделении картинки и после каждой команды.
        /// </summary>
        public void SyncFromTarget()
        {
            var info = _target.GetSelectedImageInfo();
            if (info is null) return;
            CurrentAlignment = info.Value.Align;
            CurrentWrap = info.Value.Wrap;
            IsAspectLocked = info.Value.LockAspect;
        }
    }
}
