using Avalonia.Media.Imaging;
using ReactiveUI;
using Serilog;
using System;
using Writersword.Modules.Characters.Interfaces;

namespace Writersword.Modules.Characters.ViewModels.Tabs
{
    /// <summary>
    /// Картинка из галереи персонажа. Хранит ссылку на файл в проекте и его
    /// уменьшенное изображение для показа.
    ///
    /// Превью грузится ограниченного размера: в галерее два десятка картинок,
    /// и держать их в полном разрешении ради плиток 96 пикселей — верный способ
    /// съесть память на проекте с тремя сотнями персонажей.
    /// </summary>
    public class CharacterGalleryItemViewModel : ReactiveObject, IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterGalleryItemViewModel>();

        private const int PreviewMaxSide = 240;

        public string ImageRef { get; }

        /// <summary>
        /// Плитка «добавить». Стоит в общей сетке последней, а не отдельной
        /// кнопкой под галереей: так она попадает в тот же ряд, что картинки,
        /// и читается как следующее место, а не как отдельный орган.
        /// </summary>
        public bool IsAddTile { get; }

        /// <summary>
        /// Место, которое картинка займёт, если её сейчас отпустить. На время
        /// переноса сама картинка из списка убирается, а вместо неё в сетке
        /// стоит эта копия — так соседи расступаются по-настоящему, а не
        /// рисуют подсветку края.
        /// </summary>
        public bool IsPlaceholder { get; init; }

        /// <summary>Плитка добавления — своя, без картинки.</summary>
        public CharacterGalleryItemViewModel()
        {
            ImageRef = string.Empty;
            IsAddTile = true;
        }

        /// <summary>
        /// Копия для места вставки. Превью берётся у оригинала и не
        /// освобождается вместе с копией: картинка одна, владелец у неё тоже
        /// один, иначе после переноса плитка осталась бы пустой.
        /// </summary>
        public CharacterGalleryItemViewModel(CharacterGalleryItemViewModel source)
        {
            ImageRef = source.ImageRef;
            IsAddTile = source.IsAddTile;
            _preview = source.Preview;
            _ownsPreview = false;
        }

        private readonly bool _ownsPreview = true;

        private Bitmap? _preview;
        public Bitmap? Preview
        {
            get => _preview;
            private set => this.RaiseAndSetIfChanged(ref _preview, value);
        }

        public CharacterGalleryItemViewModel(string imageRef, ICharacterAvatarService? avatarService)
        {
            ImageRef = imageRef;

            if (avatarService == null || string.IsNullOrEmpty(imageRef)) return;

            try
            {
                _preview = avatarService.LoadBitmap(imageRef, PreviewMaxSide);
            }
            catch (Exception ex)
            {
                // Битый или пропавший файл не должен ронять карточку: место
                // в галерее останется пустым, ссылку можно убрать руками.
                _logger.Error(ex, "Gallery preview load failed: {Ref}", imageRef);
            }
        }

        public void Dispose()
        {
            if (_ownsPreview) _preview?.Dispose();
            _preview = null;
        }
    }
}
