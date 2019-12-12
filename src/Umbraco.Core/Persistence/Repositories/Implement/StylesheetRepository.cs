﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using Umbraco.Core.Composing;
using Umbraco.Core.IO;
using Umbraco.Core.Models;

namespace Umbraco.Core.Persistence.Repositories.Implement
{
    /// <summary>
    /// Represents the Stylesheet Repository
    /// </summary>
    internal class StylesheetRepository : FileRepository<string, IStylesheet>, IStylesheetRepository
    {
        private readonly IIOHelper _ioHelper;

        public StylesheetRepository(IFileSystems fileSystems, IIOHelper ioHelper)
            : base(fileSystems.StylesheetsFileSystem)
        {
            _ioHelper = ioHelper;
        }

        #region Overrides of FileRepository<string,Stylesheet>

        public override IStylesheet Get(string id)
        {
            // get the relative path within the filesystem
            // (though... id should be relative already)
            var path = FileSystem.GetRelativePath(id);

            path = path.EnsureEndsWith(".css");

            if (FileSystem.FileExists(path) == false)
                return null;

            // content will be lazy-loaded when required
            var created = FileSystem.GetCreated(path).UtcDateTime;
            var updated = FileSystem.GetLastModified(path).UtcDateTime;
            //var content = GetFileContent(path);

            var stylesheet = new Stylesheet(path, file => GetFileContent(file.OriginalPath))
            {
                //Content = content,
                Key = path.EncodeAsGuid(),
                CreateDate = created,
                UpdateDate = updated,
                Id = path.GetHashCode(),
                VirtualPath = FileSystem.GetUrl(path)
            };

            // reset dirty initial properties (U4-1946)
            stylesheet.ResetDirtyProperties(false);

            return stylesheet;

        }

        public override void Save(IStylesheet entity)
        {
            // TODO: Casting :/ Review GetFileContent and it's usages, need to look into it later
            var stylesheet = (Stylesheet)entity;

            base.Save(stylesheet);

            // ensure that from now on, content is lazy-loaded
            if (stylesheet.GetFileContent == null)
                stylesheet.GetFileContent = file => GetFileContent(file.OriginalPath);
        }

        public override IEnumerable<IStylesheet> GetMany(params string[] ids)
        {
            //ensure they are de-duplicated, easy win if people don't do this as this can cause many excess queries
            ids = ids
                .Select(x => x.EnsureEndsWith(".css"))
                .Distinct()
                .ToArray();

            if (ids.Any())
            {
                foreach (var id in ids)
                {
                    yield return Get(id);
                }
            }
            else
            {
                var files = FindAllFiles("", "*.css");
                foreach (var file in files)
                {
                    yield return Get(file);
                }
            }
        }

        /// <summary>
        /// Gets a list of all <see cref="Stylesheet"/> that exist at the relative path specified.
        /// </summary>
        /// <param name="rootPath">
        /// If null or not specified, will return the stylesheets at the root path relative to the IFileSystem
        /// </param>
        /// <returns></returns>
        public IEnumerable<IStylesheet> GetStylesheetsAtPath(string rootPath = null)
        {
            return FileSystem.GetFiles(rootPath ?? string.Empty, "*.css").Select(Get);
        }

        private static readonly List<string> ValidExtensions = new List<string> { "css" };

        public bool ValidateStylesheet(IStylesheet stylesheet)
        {
            // get full path
            string fullPath;
            try
            {
                // may throw for security reasons
                fullPath = FileSystem.GetFullPath(stylesheet.Path);
            }
            catch
            {
                return false;
            }

            // validate path and extension
            var validDir = Current.Configs.Global().UmbracoCssPath;
            var isValidPath = _ioHelper.VerifyEditPath(fullPath, validDir);
            var isValidExtension = _ioHelper.VerifyFileExtension(stylesheet.Path, ValidExtensions);
            return isValidPath && isValidExtension;
        }

        public Stream GetFileContentStream(string filepath)
        {
            if (FileSystem.FileExists(filepath) == false) return null;

            try
            {
                return FileSystem.OpenFile(filepath);
            }
            catch
            {
                return null; // deal with race conds
            }
        }

        public void SetFileContent(string filepath, Stream content)
        {
            FileSystem.AddFile(filepath, content, true);
        }

        public new long GetFileSize(string filepath)
        {
            if (FileSystem.FileExists(filepath) == false) return -1;

            try
            {
                return FileSystem.GetSize(filepath);
            }
            catch
            {
                return -1; // deal with race conds
            }
        }

        #endregion
    }
}