using System;
using Writersword.Core.Interfaces.Workspace;
using Writersword.Core.Models.Project;
using Writersword.Core.Services;

namespace Writersword.Core.Interfaces.WorkFlows
{
    /// <summary>
    /// Абстракция вкладки документа для Core-интерфейсов.
    /// Реализуется DocumentTabViewModel в App-слое.
    /// </summary>
    public interface IDocumentTab : IDisposable
    {
        string? FilePath { get; set; }
        string Title { get; set; }
        bool IsLoaded { get; }
        bool IsActive { get; set; }
        DocumentContext? Context { get; }
        IWorkspaceController? Workspace { get; }

        ProjectFile GetProject();
        void MarkAsModified();
        void UpdateProject(ProjectFile project);
    }
}