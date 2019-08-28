﻿using JetBrains.Annotations;
using System.Collections.Generic;

namespace Mailr.Data
{
    [UsedImplicitly]
    public class Extensibility
    {
        public string Ext { get; set; }

        public string Bin { get; set; }

        public Development Development { get; set; }
    }

    [UsedImplicitly]
    public class Development
    {
        public IEnumerable<string> Bins { get; set; }

        public IEnumerable<Extension> Extensions { get; set; }
    }

    [UsedImplicitly]
    public class Extension
    {
        public string SolutionDirectory { get; set; }

        public IEnumerable<string> Projects { get; set; }
    }
}
