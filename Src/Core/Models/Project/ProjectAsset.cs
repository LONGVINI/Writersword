using System.Collections.Generic;

namespace Writersword.Core.Models.Project
{
    /// <summary>Что это за файл. От вида зависит, как о нём говорить человеку.</summary>
    public enum ProjectAssetKind
    {
        /// <summary>Картинка: аватарка, бумага, фон, значок.</summary>
        Image = 0,

        /// <summary>Файл шрифта.</summary>
        Font = 1,

        /// <summary>Всё прочее.</summary>
        Other = 2
    }

    /// <summary>
    /// Где лежит файл. Это и есть ответ на вопрос «уедет ли он вместе с
    /// проектом», а он важнее всех остальных: проект, половина картинок
    /// которого лежит на диске автора, у второго человека выглядит иначе, и
    /// узнать об этом ему неоткуда.
    /// </summary>
    public enum ProjectAssetPlace
    {
        /// <summary>В архиве проекта. Уедет.</summary>
        InProject = 0,

        /// <summary>В данных программы. Не уедет: у другого человека их нет.</summary>
        InApp = 1,

        /// <summary>В ресурсах сборки. Уедет — есть у каждого, кто запустил программу.</summary>
        BuiltIn = 2,

        /// <summary>Путь к файлу на диске. Не уедет и потеряется при переносе папки.</summary>
        OnDisk = 3,

        /// <summary>Ссылка есть, файла нет. Показывать нечего уже сейчас.</summary>
        Missing = 4
    }

    /// <summary>Почему файл нужен.</summary>
    public enum ProjectAssetHold
    {
        /// <summary>На него ссылается содержимое проекта: персонаж, страница, вид.</summary>
        Used = 0,

        /// <summary>
        /// Он лежит в наборе проекта — в паке аватарок, в подборке видов. Ссылок
        /// на него может не быть вовсе, и это не повод его убирать: набор
        /// собирают заранее и пользуются им потом.
        /// </summary>
        Stored = 1
    }

    /// <summary>
    /// Одна ссылка модуля на файл. Модули отдают такие описания слою проекта,
    /// и по ним собирается ответ на два вопроса: что не уедет вместе с проектом
    /// и чего уже не хватает.
    ///
    /// Сам файл здесь не лежит и путь к нему не раскрывается: как устроен адрес,
    /// знает только тот модуль, который его завёл.
    /// </summary>
    public sealed class ProjectAssetRef
    {
        /// <summary>Адрес в том виде, в каком его понимает модуль.</summary>
        public string Ref { get; init; } = string.Empty;

        /// <summary>Модуль, который держит ссылку: «TextEditor», «Characters».</summary>
        public string ModuleType { get; init; } = string.Empty;

        public ProjectAssetKind Kind { get; init; } = ProjectAssetKind.Image;
        public ProjectAssetPlace Place { get; init; } = ProjectAssetPlace.InProject;
        public ProjectAssetHold Hold { get; init; } = ProjectAssetHold.Used;

        /// <summary>Имя файла — то, что человек узнает в списке.</summary>
        public string DisplayName { get; init; } = string.Empty;

        /// <summary>
        /// Кто держит ссылку, словами: «Персонаж Марк», «Вид чтения Ночная»,
        /// «Папка Портреты». Без этого список недостающих файлов превращается в
        /// перечень имён, по которому непонятно, где искать.
        /// </summary>
        public string? OwnerName { get; init; }

        /// <summary>Размер файла в байтах. 0 — неизвестен или файла нет.</summary>
        public long Bytes { get; init; }

        /// <summary>Файл уедет вместе с проектом.</summary>
        public bool TravelsWithProject =>
            Place == ProjectAssetPlace.InProject || Place == ProjectAssetPlace.BuiltIn;

        /// <summary>Файл нужно уложить в проект, чтобы он уехал.</summary>
        public bool NeedsEmbedding =>
            Place == ProjectAssetPlace.InApp || Place == ProjectAssetPlace.OnDisk;
    }

    /// <summary>Итог уборки: сколько файлов убрано и сколько места освободилось.</summary>
    public sealed class ProjectAssetCleanup
    {
        public static readonly ProjectAssetCleanup Nothing = new();

        public int Removed { get; init; }
        public long FreedBytes { get; init; }

        public static ProjectAssetCleanup operator +(ProjectAssetCleanup a, ProjectAssetCleanup b)
            => new() { Removed = a.Removed + b.Removed, FreedBytes = a.FreedBytes + b.FreedBytes };
    }

    /// <summary>
    /// Что известно о файлах проекта: всё вместе, что не уедет и чего не
    /// хватает. Отчёт собирается по требованию и ничего не меняет.
    /// </summary>
    public sealed class ProjectAssetReport
    {
        public IReadOnlyList<ProjectAssetRef> All { get; init; } = new List<ProjectAssetRef>();

        /// <summary>Файлы, которые не уедут вместе с проектом.</summary>
        public IReadOnlyList<ProjectAssetRef> Outside { get; init; } = new List<ProjectAssetRef>();

        /// <summary>Ссылки, по которым файла уже нет.</summary>
        public IReadOnlyList<ProjectAssetRef> Missing { get; init; } = new List<ProjectAssetRef>();

        /// <summary>
        /// Проект самодостаточен: всё, что в нём видно, лежит в нём самом.
        /// Недостающие файлы самодостаточности не мешают — их нет нигде, и
        /// укладывать нечего; о них говорится отдельно.
        /// </summary>
        public bool IsSelfContained => Outside.Count == 0;

        /// <summary>Сколько места займёт укладка всего внешнего в архив.</summary>
        public long OutsideBytes
        {
            get
            {
                long sum = 0;
                foreach (var item in Outside) sum += item.Bytes;
                return sum;
            }
        }
    }
}
