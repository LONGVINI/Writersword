namespace Writersword.Modules.Characters.Models.Enums
{
    /// <summary>
    /// Режим отображения списка персонажей.
    /// Grid — псевдоним GridMedium, оставлен для обратной совместимости с сохранёнными сессиями.
    /// </summary>
    public enum CharactersViewMode
    {
        List,
        Grid,        // backward-compat alias = GridMedium
        GridSmall,
        GridMedium,
        GridLarge,
        GridHuge
    }
}