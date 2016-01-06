﻿using System;
using System.Collections.Generic;
using System.Text;

namespace Waher.Content.Markdown.Model.SpanElements
{
	/// <summary>
	/// Link
	/// </summary>
	public class Link : MarkdownElementChildren
	{
		private string url;
		private string title;

		/// <summary>
		/// Link
		/// </summary>
		/// <param name="Document">Markdown document.</param>
		/// <param name="ChildElements">Child elements.</param>
		/// <param name="Url">URL</param>
		/// <param name="Title">Optional title.</param>
		public Link(MarkdownDocument Document, LinkedList<MarkdownElement> ChildElements, string Url, string Title)
			: base(Document, ChildElements)
		{
			this.url = Url;
			this.title = Title;
		}

		/// <summary>
		/// URL
		/// </summary>
		public string Url
		{
			get { return this.url; }
		}

		/// <summary>
		/// Optional Link title.
		/// </summary>
		public string Title
		{
			get { return this.title; }
		}

		/// <summary>
		/// Generates HTML for the markdown element.
		/// </summary>
		/// <param name="Output">HTML will be output here.</param>
		public override void GenerateHTML(StringBuilder Output)
		{
			GenerateHTML(Output, this.url, this.title, this.Children);
		}

		/// <summary>
		/// Generates HTML for a link.
		/// </summary>
		/// <param name="Output">HTML will be output here.</param>
		/// <param name="Url">URL</param>
		/// <param name="Title">Optional title.</param>
		/// <param name="ChildNodes">Child nodes.</param>
		public static void GenerateHTML(StringBuilder Output, string Url, string Title, IEnumerable<MarkdownElement> ChildNodes)
		{
			Output.Append("<a href=\"");
			Output.Append(MarkdownDocument.HtmlEncode(Url));

			if (!string.IsNullOrEmpty(Title))
			{
				Output.Append("\" title=\"");
				Output.Append(MarkdownDocument.HtmlEncode(Title));
			}

			Output.Append("\">");

			foreach (MarkdownElement E in ChildNodes)
				E.GenerateHTML(Output);

			Output.Append("</a>");
		}

		/// <summary>
		/// <see cref="Object.ToString()"/>
		/// </summary>
		public override string ToString()
		{
			return this.url;
		}
	
	}
}