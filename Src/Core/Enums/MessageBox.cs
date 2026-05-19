namespace Writersword.Core.Enums
{
    public enum MessageBoxType
    {
        Info,
        Warning,
        Error,
        Question,
        Recovery
    }

    public enum MessageBoxButtons
    {
        OK,
        OKCancel,
        YesNo,
        YesNoCancel,
        Recovery
    }

    public enum MessageBoxResult
    {
        None,
        OK,
        Cancel,
        Yes,
        No,
        Restore,
        OpenSaved,
        Compare
    }
}