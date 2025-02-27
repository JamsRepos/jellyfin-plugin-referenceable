﻿using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;

namespace Jellyfin.Plugin.Referenceable.Helpers
{
    public static class PatchHelper
    {
        private static Harmony s_harmony = new Harmony("dev.iamparadox.jellyfin");

        internal static void SetupPatches()
        {
            HarmonyMethod patchMethod = new HarmonyMethod(typeof(PatchHelper).GetMethod(nameof(Patch_Harmony_Patch), BindingFlags.Static | BindingFlags.NonPublic));
            patchMethod.priority = Priority.First;
            
            HarmonyMethod createPluginInstanceMethod = new HarmonyMethod(typeof(PatchHelper).GetMethod(nameof(Patch_PluginManager_CreatePluginInstance), BindingFlags.Static | BindingFlags.NonPublic));

            HarmonyMethod configureStartupPatchMethod = new HarmonyMethod(typeof(StartupHelper).GetMethod(nameof(StartupHelper.Patch_Startup_Configure), BindingFlags.NonPublic | BindingFlags.Static));

            HarmonyMethod getApiPluginAssembliesMethod = new HarmonyMethod(typeof(PatchHelper).GetMethod(nameof(Patch_ServerApplicationHost_GetApiPluginAssemblies), BindingFlags.Static | BindingFlags.NonPublic));
            
            // Setup a patch to stop calls to patch functions when the call has come from another assembly.
            s_harmony.Patch(typeof(Harmony).GetMethod(nameof(Harmony.Patch)), 
                prefix: patchMethod);
            
            // We need to make sure that the plugin instance of a referenceable plugin is created from the non collectible assembly,
            // so we patch this to change the type to the non collectible one where its available.
            Type pluginManagerType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => x.GetTypes()).FirstOrDefault(x => x.Name == "PluginManager")!;
            s_harmony.Patch(pluginManagerType.GetMethod("CreatePluginInstance", BindingFlags.NonPublic | BindingFlags.Instance),
                prefix: createPluginInstanceMethod);
            
            // We patch the Startup.Configure function to allow things to be changed while the app is being setup.
            // Currently the only configurable element is the FileProvider for Default/Static files for /web but 
            // as there are more requirements this will update to include those too.
            Type startupType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => x.GetTypes()).FirstOrDefault(x => x.Name == "Startup")!;
            s_harmony.Patch(startupType.GetMethod("Configure"),
                prefix: configureStartupPatchMethod);
            
            // We patch the ApplicationHost.GetApiPluginAssemblies function to allow us to change the assemblies that are
            // returned for assemblies that have been reloaded into our collectible context.
            Type applicationHostType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => x.GetTypes()).FirstOrDefault(x => x.Name == "ApplicationHost")!;
            s_harmony.Patch(applicationHostType.GetMethod("GetApiPluginAssemblies"),
                prefix: getApiPluginAssembliesMethod);
        }

        private static bool Patch_Harmony_Patch(MethodBase original, HarmonyMethod? prefix = null,
            HarmonyMethod? postfix = null, HarmonyMethod? transpiler = null, HarmonyMethod? finalizer = null)
        {
            // This is probably not perfect and might need changing.
            Assembly? attemptingPatchAssembly = (new StackTrace()).GetFrames().Skip(2).First().GetMethod()?.DeclaringType?.Assembly;
            if (attemptingPatchAssembly != Assembly.GetExecutingAssembly())
            {
                Console.WriteLine($"Patching functions can only be called from assembly '{Assembly.GetExecutingAssembly().FullName}'");
                return false;
            }

            return true;
        }

        private static void Patch_PluginManager_CreatePluginInstance(ref Type type)
        {
            string pluginAssemblyFullName = type.Assembly.FullName;
            IEnumerable<Assembly> assembliesContainingType = AssemblyLoadContext.All
                .SelectMany(x => x.Assemblies)
                .Where(x => x.FullName == pluginAssemblyFullName);

            if (assembliesContainingType.Any(x => !x.IsCollectible))
            {
                Assembly assemblyToUse = assembliesContainingType.First(x => !x.IsCollectible);
                
                string typeFullName = type.FullName;
                Type? replacementType = assemblyToUse.GetTypes().FirstOrDefault(x => x.FullName == typeFullName);

                if (replacementType != null)
                {
                    type = replacementType;
                }
            }
        }

        private static void Patch_ServerApplicationHost_GetApiPluginAssemblies(ref Type[] ____allConcreteTypes)
        {
            // This is the earliest point we can replace the _allConcreteTypes array with the correct values.
            AssemblyLoadContext refPluginLoadContext = AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly());
            
            IEnumerable<Type> newPotentialConcreteTypes = refPluginLoadContext.Assemblies
                .SelectMany(x => x.GetExportedTypes())
                .Where(x => x.IsClass && !x.IsAbstract && !x.IsInterface && !x.IsGenericType);
            
            List<Type> concreteTypes = new List<Type>();
            concreteTypes.AddRange(____allConcreteTypes);
            concreteTypes.AddRange(newPotentialConcreteTypes);

            IEnumerable<IGrouping<string?, Type>> groupedTypes = concreteTypes.GroupBy(x => x.FullName);
            
            IEnumerable<Type> finalTypes = groupedTypes.Select(x =>
            {
                if (x.Any(y => !y.Assembly.IsCollectible))
                {
                    return x.First(y => !y.Assembly.IsCollectible);
                }

                if (x.Count() == 1)
                {
                    return x.Single();
                }

                if (x.Any())
                {
                    return x.First();
                }

                return null;
            }).Where(x => x != null).Select(x => x!);

            ____allConcreteTypes = finalTypes.ToArray();
        }
    }
}