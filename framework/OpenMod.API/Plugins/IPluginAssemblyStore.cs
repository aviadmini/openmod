﻿using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace OpenMod.API.Plugins
{
    /// <summary>
    /// The plugin assembly store used during OpenMod startup.
    /// </summary>
    /// <remarks>
    /// <b>This is an interface is for internal usage only and should not be used by plugins.</b>
    /// </remarks>
    [OpenModInternal]
    public interface IPluginAssemblyStore
    {
        /// <summary>
        /// Loads plugin assemblies from the given assembly source.
        /// </summary>
        /// <param name="source">The assemblies source.</param>
        /// <returns>The loaded plugin asemblies.</returns>
        [OpenModInternal]
        Task<ICollection<Assembly>> LoadPluginAssembliesAsync(IPluginAssembliesSource source);

        /// <value>
        /// The loaded plugin assemblies.
        /// </value>
        [OpenModInternal]
        IReadOnlyCollection<Assembly> LoadedPluginAssemblies { get; }
    }
}