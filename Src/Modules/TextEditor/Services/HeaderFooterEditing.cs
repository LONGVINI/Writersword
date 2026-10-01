using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Page;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Итог правки места колонтитула прямо на листе.
    /// </summary>
    public sealed class HeaderFooterEditResult
    {
        /// <summary>Новый текст места для всех листов этого варианта. Null — шаблон не менялся.</summary>
        public string? NewTemplate { get; set; }

        /// <summary>Правила этого листа, которые правка сняла.</summary>
        public List<Guid> RemovedRuleIds { get; } = new();

        /// <summary>Правила, которые правка завела.</summary>
        public List<PageRule> AddedRules { get; } = new();

        /// <summary>
        /// Набранное вместо номера число разошлось со счётом. Здесь оно, чтобы
        /// предложить «продолжить счёт отсюда». Null — предлагать нечего.
        /// </summary>
        public int? MismatchNumber { get; set; }

        /// <summary>Номер, который стоял бы на листе по счёту.</summary>
        public string? ExpectedNumberText { get; set; }

        /// <summary>Правка ничего не изменила.</summary>
        public bool IsEmpty => NewTemplate is null && RemovedRuleIds.Count == 0 && AddedRules.Count == 0;
    }

    /// <summary>
    /// Разбор правки места колонтитула на листе.
    ///
    /// Правка номера и правка текста — разные вещи, и разводятся они здесь. Текст
    /// вокруг номера — шаблон: поправили на одном листе, поменялось на всех листах
    /// этого варианта, как в Word. А номер — свойство листа:
    ///   • стёрли номер — номер убран только на этом листе, счёт идёт дальше;
    ///   • набрали номер, совпадающий со счётом, — номер снова живой;
    ///   • набрали другое число — на листе стоит набранное, счёт не тронут, а человек
    ///     получает вопрос, не продолжить ли счёт отсюда.
    /// Word в двух последних случаях молча заменяет живое поле мёртвым текстом, и
    /// номер больше не обновляется никогда — даже набранный правильно.
    /// </summary>
    public static class HeaderFooterEditing
    {
        /// <summary>
        /// Разбирает правку одного места колонтитула на одном листе.
        /// </summary>
        /// <param name="settings">Колонтитулы документа.</param>
        /// <param name="decoration">Оформление листа до правки.</param>
        /// <param name="header">Верхний колонтитул (иначе нижний).</param>
        /// <param name="slot">Место: 0 — лево, 1 — центр, 2 — право.</param>
        /// <param name="editedText">Текст места после правки.</param>
        /// <param name="anchorPages">Абзац-метка → лист, где он начинается.</param>
        /// <param name="evaluate">Пересчёт оформления этого же листа для других настроек.</param>
        public static HeaderFooterEditResult Resolve(
            HeaderFooterSettings settings,
            PageDecoration decoration,
            bool header,
            int slot,
            string editedText,
            IReadOnlyDictionary<Guid, int>? anchorPages,
            Func<HeaderFooterSettings, PageDecoration?> evaluate)
        {
            var result = new HeaderFooterEditResult();
            editedText ??= string.Empty;

            var band = settings.GetBand(decoration.Variant, header);
            string template = band.GetSlot(slot);
            var rendered = PageNumbering.RenderForEditing(template, decoration);

            if (string.Equals(rendered.Text, editedText, StringComparison.Ordinal))
                return result;

            string before = rendered.Text;
            int prefix = CommonPrefix(before, editedText);
            int suffix = CommonSuffix(before, editedText, prefix);

            int changeStart = prefix;
            int changeEndOld = before.Length - suffix;
            int delta = editedText.Length - before.Length;

            // Правка целиком внутри номера — это правка номера, а не шаблона.
            foreach (var field in rendered.Fields)
            {
                if (field.Token != HeaderFooterSettings.PageToken) continue;
                if (changeStart < field.Start || changeEndOld > field.End) continue;

                int newLength = field.Length + delta;
                if (newLength < 0 || field.Start + newLength > editedText.Length) break;

                string typed = editedText.Substring(field.Start, newLength);
                ResolveNumberEdit(settings, decoration, typed, anchorPages, evaluate, result);
                return result;
            }

            result.NewTemplate = RebuildTemplate(editedText, rendered, changeStart, changeEndOld, delta);
            if (string.Equals(result.NewTemplate, template, StringComparison.Ordinal))
                result.NewTemplate = null;

            return result;
        }

        /// <summary>
        /// Правка самого номера. Прежние правила этого листа про номер снимаются, и
        /// решение принимается заново от того, каким номер был бы без них.
        /// </summary>
        private static void ResolveNumberEdit(
            HeaderFooterSettings settings,
            PageDecoration decoration,
            string typed,
            IReadOnlyDictionary<Guid, int>? anchorPages,
            Func<HeaderFooterSettings, PageDecoration?> evaluate,
            HeaderFooterEditResult result)
        {
            int page = decoration.PageIndex;

            var probe = settings.Clone();
            foreach (var rule in settings.Rules)
            {
                if (rule.IsRange) continue;
                if (!IsNumberAction(rule.Action)) continue;
                if (PageNumbering.TargetPage(rule, anchorPages) != page) continue;

                result.RemovedRuleIds.Add(rule.Id);
                probe.Rules.RemoveAll(r => r.Id == rule.Id);
            }

            var baseline = evaluate(probe) ?? decoration;
            string value = typed.Trim();

            if (value.Length == 0)
            {
                // Стёрли номер: на этом листе его нет, счёт идёт своим чередом.
                if (baseline.NumberVisible)
                    result.AddedRules.Add(NewPageRule(page, PageRuleAction.HideNumber));
                return;
            }

            if (string.Equals(value, baseline.NumberText, StringComparison.Ordinal))
            {
                // Набрали верный номер — номер снова живой. Если его прятал отрезок
                // «дальше не надо», этому листу номер возвращается отдельно.
                if (!baseline.NumberVisible)
                    result.AddedRules.Add(NewPageRule(page, PageRuleAction.ShowNumber));
                return;
            }

            // Другое число. Счёт не трогается молча: набранное встаёт только на этот
            // лист, а продолжить ли счёт отсюда — решает человек.
            var manual = NewPageRule(page, PageRuleAction.ManualNumber);
            manual.ManualText = value;
            result.AddedRules.Add(manual);

            result.ExpectedNumberText = baseline.NumberText;
            result.MismatchNumber = PageNumbering.ParseNumber(value, baseline.Format);
        }

        private static bool IsNumberAction(PageRuleAction action)
            => action == PageRuleAction.HideNumber
               || action == PageRuleAction.ShowNumber
               || action == PageRuleAction.ManualNumber;

        private static PageRule NewPageRule(int page, PageRuleAction action) => new()
        {
            Scope = PageRuleScope.Page,
            PageIndex = page,
            Action = action
        };

        /// <summary>
        /// Шаблон места из правленого текста: поля, которых правка не коснулась,
        /// возвращаются полями. Поле, по которому прошла правка, становится обычным
        /// текстом — человек его переписал.
        /// </summary>
        private static string RebuildTemplate(
            string edited, RenderedSlot rendered, int changeStart, int changeEndOld, int delta)
        {
            var kept = new List<SlotFieldSpan>();

            foreach (var field in rendered.Fields)
            {
                if (field.Length == 0)
                {
                    if (field.Start <= changeStart) kept.Add(field);
                    else if (field.Start >= changeEndOld) kept.Add(field with { Start = field.Start + delta });
                    continue;
                }

                if (field.End <= changeStart) kept.Add(field);
                else if (field.Start >= changeEndOld) kept.Add(field with { Start = field.Start + delta });
            }

            if (kept.Count == 0) return edited;

            kept.Sort((a, b) => a.Start.CompareTo(b.Start));

            var sb = new System.Text.StringBuilder(edited.Length + kept.Count * 6);
            int pos = 0;
            foreach (var field in kept)
            {
                if (field.Start < pos || field.End > edited.Length) continue;
                sb.Append(edited, pos, field.Start - pos);
                sb.Append(field.Token);
                pos = field.End;
            }
            sb.Append(edited, pos, edited.Length - pos);
            return sb.ToString();
        }

        private static int CommonPrefix(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length);
            int i = 0;
            while (i < n && a[i] == b[i]) i++;
            return i;
        }

        private static int CommonSuffix(string a, string b, int prefix)
        {
            int n = Math.Min(a.Length, b.Length) - prefix;
            int i = 0;
            while (i < n && a[a.Length - 1 - i] == b[b.Length - 1 - i]) i++;
            return i;
        }
    }
}
