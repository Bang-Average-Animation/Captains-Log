using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Mafi;
using Mafi.Collections;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core.Entities;
using Mafi.Core.Factory.Machines;
using Mafi.Core.Factory.Recipes;
using Mafi.Core.Mods;
using Mafi.Core.Products;
using Mafi.Core.Prototypes;

namespace CLExporter
{
	public sealed class CLExporter : DataOnlyMod, IMod, IDisposable
	{
		private IEntitiesManager m_entitiesManager;

		private int m_machineExportCount;

		private static int s_nextInstanceId;

		private static int s_activeInstanceId;

		private int m_instanceId;

		private object m_electricityManager;

		private DependencyResolver m_resolver;

		private readonly object m_exportLock = new object();

		private System.Timers.Timer m_refreshTimer;

		private const int MAX_MACHINE_EXPORTS = 200;

		private const double REFRESH_INTERVAL_MS = 10000.0;

		public CLExporter(ModManifest manifest)
			: base(manifest)
		{
			Log.Info("CLExporter: constructed");
			string text = Path.Combine(manifest.RootDirectoryPath, "coi_mod_lifecycle_signature_probe.json");
			string contents = BuildMemberProbeJson();
			File.WriteAllText(text, contents);
			Log.Info("CLExporter: wrote mod lifecycle signature probe export to " + text);
		}

		void IMod.Initialize(DependencyResolver resolver, bool gameWasLoaded)
		{
			Log.Info("CLExporter: explicit IMod.Initialize called - subscribing to entity events and timer");
			m_instanceId = Interlocked.Increment(ref s_nextInstanceId);
			Volatile.Write(ref s_activeInstanceId, m_instanceId);
			ExportCatalogue(resolver);
			Log.Info("CLExporter: active exporter instance set to " + m_instanceId);
			try
			{
				m_resolver = resolver;
				m_entitiesManager = resolver.Resolve<IEntitiesManager>();
				m_electricityManager = ResolveTypeReflective(resolver, FindTypeByFullName("Mafi.Core.Factory.ElectricPower.IElectricityManager"));
				m_entitiesManager.EntityAddedFull.AddNonSaveable(this, OnEntityAddedFull);
				StartRefreshTimer();
				ExportWorldMachines(gameWasLoaded, "initialize");
			}
			catch (Exception ex)
			{
				WriteWorldMachinesError("initialize", ex);
			}
		}

		public override void RegisterPrototypes(ProtoRegistrator registrator)
		{
		}

		private void ExportCatalogue(DependencyResolver resolver)
		{
			try
			{
				// Initialize runs after every selected mod has registered its products and machine bindings.
				ProtosDb prototypes = resolver.Resolve<ProtosDb>();
				string root = Manifest.RootDirectoryPath;
				File.WriteAllText(Path.Combine(root, "coi_products.json"), BuildProductsJson(prototypes));
				File.WriteAllText(Path.Combine(root, "coi_recipes.json"), BuildRecipesJson(prototypes));
				Log.Info("CLExporter: wrote products and recipes catalogues to " + root);
			}
			catch (Exception ex)
			{
				Log.Warning("CLExporter: could not export products and recipes catalogues: " + ex);
			}
		}

		public override void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues)
		{
		}

		private static string BuildMemberProbeJson()
		{
			string[] array = new string[11]
			{
				"Mafi.Core.Mods.DataOnlyMod", "Mafi.Core.Mods.IMod", "Mafi.Core.Mods.IModConfig", "Mafi.Core.Mods.ModJsonConfig", "Mafi.Core.Mods.ModManifest", "Mafi.Core.Mods.ProtoRegistrator", "Mafi.Core.Mods.ModDependencyResolver", "Mafi.Core.Mods.ModDependencies", "Mafi.Core.Mods.IModDependency", "Mafi.Core.Entities.IEntitiesManager",
				"Mafi.Core.Entities.EntitiesManager"
			};
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("{");
			stringBuilder.AppendLine("  \"exportType\": \"memberProbe\",");
			stringBuilder.AppendLine("  \"types\": [");
			for (int i = 0; i < array.Length; i++)
			{
				string text = array[i];
				Type type = FindType(text);
				stringBuilder.AppendLine("    {");
				stringBuilder.AppendLine("      \"name\": \"" + EscapeJson(text) + "\",");
				if (type == null)
				{
					stringBuilder.AppendLine("      \"found\": false");
				}
				else
				{
					stringBuilder.AppendLine("      \"found\": true,");
					stringBuilder.AppendLine("      \"assembly\": \"" + EscapeJson(type.Assembly.GetName().Name) + "\",");
					List<string> values = (from p in type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
						select p.PropertyType.Name + " " + p.Name into x
						orderby x
						select x).Take(120).ToList();
					List<string> values2 = (from f in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
						select f.FieldType.Name + " " + f.Name into x
						orderby x
						select x).Take(120).ToList();
					List<string> values3 = (from x in (from m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
							select m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", from p in m.GetParameters()
								select p.ParameterType.FullName + " " + p.Name) + ")").Distinct()
						orderby x
						select x).Take(160).ToList();
					WriteStringArray(stringBuilder, "properties", values, 6);
					stringBuilder.AppendLine(",");
					WriteStringArray(stringBuilder, "fields", values2, 6);
					stringBuilder.AppendLine(",");
					WriteStringArray(stringBuilder, "methods", values3, 6);
					stringBuilder.AppendLine();
				}
				stringBuilder.Append("    }");
				if (i < array.Length - 1)
				{
					stringBuilder.Append(",");
				}
				stringBuilder.AppendLine();
			}
			stringBuilder.AppendLine("  ]");
			stringBuilder.AppendLine("}");
			return stringBuilder.ToString();
		}

		private static string BuildProductsJson(ProtosDb prototypesDb)
		{
			List<ProductProto> list = (from p in prototypesDb.All<ProductProto>()
				orderby p.Id.ToString()
				select p).ToList();
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("{");
			stringBuilder.AppendLine("  \"exportType\": \"products\",");
			stringBuilder.AppendLine("  \"productCount\": " + list.Count + ",");
			stringBuilder.AppendLine("  \"products\": [");
			for (int num = 0; num < list.Count; num++)
			{
				ProductProto productProto = list[num];
				stringBuilder.AppendLine("    {");
				stringBuilder.AppendLine("      \"productId\": \"" + EscapeJson(productProto.Id.ToString()) + "\",");
				stringBuilder.AppendLine("      \"name\": \"" + EscapeJson(productProto.Strings.Name.ToString()) + "\",");
				stringBuilder.AppendLine("      \"descShort\": \"" + EscapeJson(productProto.Strings.DescShort.ToString()) + "\",");
				stringBuilder.AppendLine("      \"type\": \"" + EscapeJson(productProto.Type.ToString()) + "\",");
				stringBuilder.AppendLine("      \"iconPath\": \"" + EscapeJson(productProto.IconPath ?? "") + "\"");
				stringBuilder.Append("    }");
				if (num < list.Count - 1)
				{
					stringBuilder.Append(",");
				}
				stringBuilder.AppendLine();
			}
			stringBuilder.AppendLine("  ]");
			stringBuilder.AppendLine("}");
			return stringBuilder.ToString();
		}

		private static string BuildRecipesJson(ProtosDb prototypes)
		{
			var bindingsByRecipe = new Dictionary<RecipeProto, List<(string MachineId, MachineRecipeBinding Binding)>>();
			foreach (MachineProto machine in prototypes.All<MachineProto>().OrderBy(machine => machine.Id.Value, StringComparer.Ordinal))
			{
				foreach (MachineRecipeBinding binding in machine.RecipeBindings)
				{
					if (!bindingsByRecipe.TryGetValue(binding.Recipe, out var bindings))
					{
						bindings = new List<(string, MachineRecipeBinding)>();
						bindingsByRecipe.Add(binding.Recipe, bindings);
					}
					bindings.Add((machine.Id.Value, binding));
				}
			}

			var recipes = prototypes.All<RecipeProto>().OrderBy(recipe => recipe.Id.Value, StringComparer.Ordinal).ToList();
			var json = new StringBuilder();
			json.AppendLine("{");
			json.AppendLine("  \"exportType\": \"recipes\",");
			json.AppendLine($"  \"recipeCount\": {recipes.Count},");
			json.AppendLine("  \"recipes\": [");
			for (int index = 0; index < recipes.Count; index++)
			{
				RecipeProto recipe = recipes[index];
				bindingsByRecipe.TryGetValue(recipe, out var bindings);
				json.AppendLine("    {");
				json.AppendLine($"      \"id\": \"{EscapeJson(recipe.Id.Value)}\",");

				// Keep a deterministic default for dashboards that only understand one row per recipe.
				IRecipeForUi defaultBinding = bindings == null ? null : bindings[0].Binding.ToRecipeForUi();
				WriteRecipeDetails(json, recipe, defaultBinding, 6);
				if (bindings != null && bindings.Count > 1)
				{
					json.AppendLine(",");
					json.AppendLine("      \"machineBindings\": {");
					for (int bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
					{
						var (machineId, binding) = bindings[bindingIndex];
						json.AppendLine($"        \"{EscapeJson(machineId)}\": {{");
						WriteRecipeDetails(json, recipe, binding.ToRecipeForUi(), 10);
						json.AppendLine();
						json.AppendLine(bindingIndex < bindings.Count - 1 ? "        }," : "        }");
					}
					json.Append("      }");
				}
				json.AppendLine();
				json.AppendLine(index < recipes.Count - 1 ? "    }," : "    }");
			}
			json.AppendLine("  ]");
			json.AppendLine("}");
			return json.ToString();
		}

		private static void WriteRecipeDetails(StringBuilder json, RecipeProto recipe, IRecipeForUi binding, int indent)
		{
			string padding = new string(' ', indent);
			string duration = binding == null ? "null" : $"\"{binding.Duration.Ticks.ToString(CultureInfo.InvariantCulture)}\"";
			json.AppendLine($"{padding}\"duration\": {duration},");
			json.AppendLine($"{padding}\"inputs\": [");
			WriteRecipeInputs(json, binding?.AllUserVisibleInputs ?? recipe.AllUserVisibleInputs, indent + 2);
			json.AppendLine($"{padding}],");
			json.AppendLine($"{padding}\"outputs\": [");
			WriteRecipeOutputs(json, binding?.AllUserVisibleOutputs ?? recipe.AllUserVisibleOutputs, indent + 2);
			json.Append($"{padding}]");
		}

		private static void WriteRecipeInputs(StringBuilder sb, ImmutableArray<RecipeInput> values, int indent)
		{
			string value = new string(' ', indent);
			for (int i = 0; i < values.Length; i++)
			{
				RecipeInput recipeInput = values[i];
				sb.Append(value);
				sb.Append("{ ");
				sb.Append("\"productId\": \"" + EscapeJson(recipeInput.Product.Id.ToString()) + "\", ");
				sb.Append("\"quantity\": \"" + EscapeJson(recipeInput.Quantity.Value.ToString(CultureInfo.InvariantCulture)) + "\"");
				sb.Append(" }");
				if (i < values.Length - 1)
				{
					sb.Append(",");
				}
				sb.AppendLine();
			}
		}

		private static void WriteRecipeOutputs(StringBuilder sb, ImmutableArray<RecipeOutput> values, int indent)
		{
			string value = new string(' ', indent);
			for (int i = 0; i < values.Length; i++)
			{
				RecipeOutput recipeOutput = values[i];
				sb.Append(value);
				sb.Append("{ ");
				sb.Append("\"productId\": \"" + EscapeJson(recipeOutput.Product.Id.ToString()) + "\", ");
				sb.Append("\"quantity\": \"" + EscapeJson(recipeOutput.Quantity.Value.ToString(CultureInfo.InvariantCulture)) + "\", ");
				sb.Append("\"triggerAtStart\": " + (recipeOutput.TriggerAtStart ? "true" : "false"));
				sb.Append(" }");
				if (i < values.Length - 1)
				{
					sb.Append(",");
				}
				sb.AppendLine();
			}
		}

		private static string BuildMachineRecipesJson(ProtoRegistrator registrator)
		{
			ProtosDb prototypesDb = registrator.PrototypesDb;
			List<MachineProto> list = (from m in prototypesDb.All<MachineProto>()
				orderby m.Id.ToString()
				select m).ToList();
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("{");
			stringBuilder.AppendLine("  \"exportType\": \"machineRecipes\",");
			stringBuilder.AppendLine("  \"machineCount\": " + list.Count + ",");
			stringBuilder.AppendLine("  \"machines\": [");
			for (int num = 0; num < list.Count; num++)
			{
				MachineProto machineProto = list[num];
				stringBuilder.AppendLine("    {");
				stringBuilder.AppendLine("      \"machineId\": \"" + EscapeJson(machineProto.Id.ToString()) + "\",");
				stringBuilder.AppendLine("      \"recipes\": [");
				for (int num2 = 0; num2 < machineProto.Recipes.Count; num2++)
				{
					RecipeProto recipeProto = machineProto.Recipes[num2];
					stringBuilder.Append("        ");
					stringBuilder.Append("\"" + EscapeJson(recipeProto.Id.ToString()) + "\"");
					if (num2 < machineProto.Recipes.Count - 1)
					{
						stringBuilder.Append(",");
					}
					stringBuilder.AppendLine();
				}
				stringBuilder.AppendLine("      ]");
				stringBuilder.Append("    }");
				if (num < list.Count - 1)
				{
					stringBuilder.Append(",");
				}
				stringBuilder.AppendLine();
			}
			stringBuilder.AppendLine("  ]");
			stringBuilder.AppendLine("}");
			return stringBuilder.ToString();
		}

		private static string BuildCatalogueCountsJson(ProtoRegistrator registrator)
		{
			ProtosDb prototypesDb = registrator.PrototypesDb;
			List<ProductProto> list = prototypesDb.All<ProductProto>().ToList();
			List<MachineProto> list2 = prototypesDb.All<MachineProto>().ToList();
			List<RecipeProto> list3 = prototypesDb.All<RecipeProto>().ToList();
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("{");
			stringBuilder.AppendLine("  \"exportType\": \"catalogueCounts\",");
			stringBuilder.AppendLine("  \"products\": " + list.Count + ",");
			stringBuilder.AppendLine("  \"machines\": " + list2.Count + ",");
			stringBuilder.AppendLine("  \"recipes\": " + list3.Count);
			stringBuilder.AppendLine("}");
			return stringBuilder.ToString();
		}

		private static string BuildTypeProbeJson()
		{
			string[] keywords = new string[6] { "Proto", "Recipe", "Product", "Machine", "Registrator", "Manager" };
			var list = (from t in (from a in AppDomain.CurrentDomain.GetAssemblies()
					where a.GetName().Name.StartsWith("Mafi", StringComparison.OrdinalIgnoreCase) || a.GetName().Name.StartsWith("Assembly", StringComparison.OrdinalIgnoreCase)
					select a).SelectMany(delegate(Assembly a)
				{
					try
					{
						return a.GetTypes();
					}
					catch
					{
						return Array.Empty<Type>();
					}
				})
				where keywords.Any((string k) => t.FullName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
				select new
				{
					Assembly = t.Assembly.GetName().Name,
					Name = t.FullName
				} into t
				orderby t.Assembly, t.Name
				select t).Take(2000).ToList();
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("{");
			stringBuilder.AppendLine("  \"exportType\": \"typeProbe\",");
			stringBuilder.AppendLine("  \"matchCount\": " + list.Count + ",");
			stringBuilder.AppendLine("  \"types\": [");
			for (int num = 0; num < list.Count; num++)
			{
				var anon = list[num];
				stringBuilder.Append("    { ");
				stringBuilder.Append("\"assembly\": \"" + EscapeJson(anon.Assembly) + "\", ");
				stringBuilder.Append("\"name\": \"" + EscapeJson(anon.Name) + "\"");
				stringBuilder.Append(" }");
				if (num < list.Count - 1)
				{
					stringBuilder.Append(",");
				}
				stringBuilder.AppendLine();
			}
			stringBuilder.AppendLine("  ]");
			stringBuilder.AppendLine("}");
			return stringBuilder.ToString();
		}

		private static string BuildRuntimeTypeProbeJson()
		{
			var list = (from t in (from a in AppDomain.CurrentDomain.GetAssemblies()
					where a.GetName().Name.StartsWith("Mafi", StringComparison.OrdinalIgnoreCase)
					select a).SelectMany(delegate(Assembly a)
				{
					try
					{
						return a.GetTypes();
					}
					catch
					{
						return Array.Empty<Type>();
					}
				})
				where t.FullName.IndexOf("Machine", StringComparison.OrdinalIgnoreCase) >= 0 || t.FullName.IndexOf("Entity", StringComparison.OrdinalIgnoreCase) >= 0 || t.FullName.IndexOf("Manager", StringComparison.OrdinalIgnoreCase) >= 0 || t.FullName.IndexOf("World", StringComparison.OrdinalIgnoreCase) >= 0
				select new
				{
					Assembly = t.Assembly.GetName().Name,
					Name = t.FullName
				} into t
				orderby t.Assembly, t.Name
				select t).Take(3000).ToList();
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("{");
			stringBuilder.AppendLine("  \"exportType\": \"runtimeTypeProbe\",");
			stringBuilder.AppendLine("  \"matchCount\": " + list.Count + ",");
			stringBuilder.AppendLine("  \"types\": [");
			for (int num = 0; num < list.Count; num++)
			{
				var anon = list[num];
				stringBuilder.Append("    { ");
				stringBuilder.Append("\"assembly\": \"" + EscapeJson(anon.Assembly) + "\", ");
				stringBuilder.Append("\"name\": \"" + EscapeJson(anon.Name) + "\"");
				stringBuilder.Append(" }");
				if (num < list.Count - 1)
				{
					stringBuilder.Append(",");
				}
				stringBuilder.AppendLine();
			}
			stringBuilder.AppendLine("  ]");
			stringBuilder.AppendLine("}");
			return stringBuilder.ToString();
		}

		private static string BuildRuntimeObjectProbeJson(object target)
		{
			Type type = target.GetType();
			List<string> values = (from p in type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				select p.PropertyType.FullName + " " + p.Name into x
				orderby x
				select x).Take(120).ToList();
			List<string> values2 = (from f in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				select f.FieldType.FullName + " " + f.Name into x
				orderby x
				select x).Take(120).ToList();
			List<string> values3 = (from x in (from m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
					select m.ReturnType.FullName + " " + m.Name + "(" + string.Join(", ", from p in m.GetParameters()
						select p.ParameterType.FullName + " " + p.Name) + ")").Distinct()
				orderby x
				select x).Take(160).ToList();
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("{");
			stringBuilder.AppendLine("  \"exportType\": \"runtimeObjectProbe\",");
			stringBuilder.AppendLine("  \"runtimeType\": \"" + EscapeJson(type.FullName) + "\",");
			stringBuilder.AppendLine("  \"assembly\": \"" + EscapeJson(type.Assembly.GetName().Name) + "\",");
			WriteStringArray(stringBuilder, "properties", values, 2);
			stringBuilder.AppendLine(",");
			WriteStringArray(stringBuilder, "fields", values2, 2);
			stringBuilder.AppendLine(",");
			WriteStringArray(stringBuilder, "methods", values3, 2);
			stringBuilder.AppendLine();
			stringBuilder.AppendLine("}");
			return stringBuilder.ToString();
		}

		private static string BuildPowerFarmTypeProbeJson()
		{
			string[] keywords = new string[19]
			{
				"Power", "Electric", "Electricity", "Generator", "Grid", "Energy", "Turbine", "Boiler", "Solar", "Nuclear",
				"Reactor", "Farm", "Crop", "Field", "Harvest", "Irrigation", "Fertility", "Greenhouse", "Food"
			};
			var list = (from t in (from a in AppDomain.CurrentDomain.GetAssemblies()
					where a.GetName().Name.StartsWith("Mafi", StringComparison.OrdinalIgnoreCase)
					select a).SelectMany(delegate(Assembly a)
				{
					try
					{
						return a.GetTypes();
					}
					catch
					{
						return Array.Empty<Type>();
					}
				})
				where t.FullName != null && keywords.Any((string k) => t.FullName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
				select new
				{
					Assembly = t.Assembly.GetName().Name,
					Name = t.FullName,
					BaseType = ((t.BaseType == null) ? "" : t.BaseType.FullName),
					IsInterface = t.IsInterface,
					IsAbstract = t.IsAbstract
				} into t
				orderby t.Assembly, t.Name
				select t).Take(5000).ToList();
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("{");
			stringBuilder.AppendLine("  \"exportType\": \"powerFarmTypeProbe\",");
			stringBuilder.AppendLine("  \"matchCount\": " + list.Count + ",");
			stringBuilder.AppendLine("  \"types\": [");
			for (int num = 0; num < list.Count; num++)
			{
				var anon = list[num];
				stringBuilder.Append("    { ");
				stringBuilder.Append("\"assembly\": \"" + EscapeJson(anon.Assembly) + "\", ");
				stringBuilder.Append("\"name\": \"" + EscapeJson(anon.Name) + "\", ");
				stringBuilder.Append("\"baseType\": \"" + EscapeJson(anon.BaseType) + "\", ");
				stringBuilder.Append("\"isInterface\": " + (anon.IsInterface ? "true" : "false") + ", ");
				stringBuilder.Append("\"isAbstract\": " + (anon.IsAbstract ? "true" : "false"));
				stringBuilder.Append(" }");
				if (num < list.Count - 1)
				{
					stringBuilder.Append(",");
				}
				stringBuilder.AppendLine();
			}
			stringBuilder.AppendLine("  ]");
			stringBuilder.AppendLine("}");
			return stringBuilder.ToString();
		}

		private static Type FindType(string fullName)
		{
			return AppDomain.CurrentDomain.GetAssemblies().Select(delegate(Assembly a)
			{
				try
				{
					return a.GetType(fullName, throwOnError: false);
				}
				catch
				{
					return (Type)null;
				}
			}).FirstOrDefault((Type t) => t != null);
		}

		private static void WriteStringArray(StringBuilder sb, string name, List<string> values, int indent)
		{
			string text = new string(' ', indent);
			sb.AppendLine(text + "\"" + name + "\": [");
			for (int i = 0; i < values.Count; i++)
			{
				sb.Append(text + "  \"" + EscapeJson(values[i]) + "\"");
				if (i < values.Count - 1)
				{
					sb.Append(",");
				}
				sb.AppendLine();
			}
			sb.Append(text + "]");
		}

		private static string EscapeJson(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return "";
			}

			var escaped = new StringBuilder(value.Length);
			foreach (char character in value)
			{
				switch (character)
				{
					case '\\':
						escaped.Append("\\\\");
						break;
					case '"':
						escaped.Append("\\\"");
						break;
					default:
						if (character < ' ')
						{
							escaped.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
						}
						else
						{
							escaped.Append(character);
						}
						break;
				}
			}
			return escaped.ToString();
		}

		private static string BuildWorldMachinesJson(List<Machine> machines, bool gameWasLoaded, string trigger, int exportCount)
		{
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("{");
			stringBuilder.AppendLine("  \"exportType\": \"worldMachines\",");
			stringBuilder.AppendLine("  \"success\": true,");
			stringBuilder.AppendLine("  \"gameWasLoaded\": " + (gameWasLoaded ? "true" : "false") + ",");
			stringBuilder.AppendLine("  \"trigger\": \"" + EscapeJson(trigger) + "\",");
			stringBuilder.AppendLine("  \"exportCount\": " + exportCount + ",");
			stringBuilder.AppendLine("  \"machineCount\": " + machines.Count + ",");
			stringBuilder.AppendLine("  \"machines\": [");
			for (int i = 0; i < machines.Count; i++)
			{
				Machine machine = machines[i];
				List<string> assignedRecipeIds = GetAssignedRecipeIds(machine);
				string text = machine.DefaultTitle.ToString();
				string propertyString = GetPropertyString(machine, "CustomTitle");
				string text2 = CleanOptionString(propertyString);
				string value = (string.IsNullOrWhiteSpace(text2) ? text : text2);
				stringBuilder.AppendLine("    {");
				stringBuilder.AppendLine("      \"entityId\": \"" + EscapeJson(machine.Id.ToString()) + "\",");
				stringBuilder.AppendLine("      \"machineId\": \"" + EscapeJson(machine.Prototype.Id.ToString()) + "\",");
				stringBuilder.AppendLine("      \"title\": \"" + EscapeJson(text) + "\",");
				stringBuilder.AppendLine("      \"customTitleRaw\": \"" + EscapeJson(propertyString) + "\",");
				stringBuilder.AppendLine("      \"customTitle\": \"" + EscapeJson(text2) + "\",");
				stringBuilder.AppendLine("      \"displayTitle\": \"" + EscapeJson(value) + "\",");
				stringBuilder.AppendLine("      \"isConstructed\": " + (machine.IsConstructed ? "true" : "false") + ",");
				stringBuilder.AppendLine("      \"isEnabled\": " + (machine.IsEnabled ? "true" : "false") + ",");
				stringBuilder.AppendLine("      \"isPaused\": " + (machine.IsPaused ? "true" : "false") + ",");
				stringBuilder.AppendLine("      \"workedThisTick\": " + (machine.WorkedThisTick ? "true" : "false") + ",");
				stringBuilder.AppendLine("      \"currentState\": \"" + EscapeJson(machine.CurrentState.ToString()) + "\",");
				stringBuilder.AppendLine("      \"utilization\": \"" + EscapeJson(machine.Utilization.ToString()) + "\",");
				stringBuilder.AppendLine("      \"lastRecipeInProgressRaw\": \"" + EscapeJson(machine.LastRecipeInProgress.ToString()) + "\",");
				stringBuilder.AppendLine("      \"position\": \"" + EscapeJson(machine.Position3f.ToString()) + "\",");
				WriteMachineBufferArray(stringBuilder, "inputBuffers", machine, "m_inputBuffers", 6);
				stringBuilder.AppendLine(",");
				WriteMachineBufferArray(stringBuilder, "outputBuffers", machine, "m_outputBuffers", 6);
				stringBuilder.AppendLine(",");
				stringBuilder.AppendLine("      \"assignedRecipes\": [");
				for (int j = 0; j < assignedRecipeIds.Count; j++)
				{
					stringBuilder.Append("        \"" + EscapeJson(assignedRecipeIds[j]) + "\"");
					if (j < assignedRecipeIds.Count - 1)
					{
						stringBuilder.Append(",");
					}
					stringBuilder.AppendLine();
				}
				stringBuilder.AppendLine("      ]");
				stringBuilder.Append("    }");
				if (i < machines.Count - 1)
				{
					stringBuilder.Append(",");
				}
				stringBuilder.AppendLine();
			}
			stringBuilder.AppendLine("  ]");
			stringBuilder.AppendLine("}");
			return stringBuilder.ToString();
		}

		private static string BuildEntityEventsProbeJson(IEntitiesManager entitiesManager, bool gameWasLoaded)
		{
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("{");
			stringBuilder.AppendLine("  \"exportType\": \"entityEventProbe\",");
			stringBuilder.AppendLine("  \"success\": true,");
			stringBuilder.AppendLine("  \"gameWasLoaded\": " + (gameWasLoaded ? "true" : "false") + ",");
			stringBuilder.AppendLine("  \"events\": [");
			WriteRuntimeObjectSummary(stringBuilder, "EntityAdded", entitiesManager.EntityAdded, 4);
			stringBuilder.AppendLine(",");
			WriteRuntimeObjectSummary(stringBuilder, "StaticEntityAdded", entitiesManager.StaticEntityAdded, 4);
			stringBuilder.AppendLine(",");
			WriteRuntimeObjectSummary(stringBuilder, "EntityAddedFull", entitiesManager.EntityAddedFull, 4);
			stringBuilder.AppendLine();
			stringBuilder.AppendLine("  ]");
			stringBuilder.AppendLine("}");
			return stringBuilder.ToString();
		}

		private static void WriteRuntimeObjectSummary(StringBuilder sb, string label, object target, int indent)
		{
			string text = new string(' ', indent);
			if (target == null)
			{
				sb.AppendLine(text + "{");
				sb.AppendLine(text + "  \"label\": \"" + EscapeJson(label) + "\",");
				sb.AppendLine(text + "  \"isNull\": true");
				sb.Append(text + "}");
				return;
			}
			Type type = target.GetType();
			List<string> values = (from x in (from m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
					select m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", from p in m.GetParameters()
						select p.ParameterType.FullName + " " + p.Name) + ")").Distinct()
				orderby x
				select x).Take(160).ToList();
			List<string> values2 = (from p in type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				select p.PropertyType.FullName + " " + p.Name into x
				orderby x
				select x).Take(80).ToList();
			List<string> values3 = (from f in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				select f.FieldType.FullName + " " + f.Name into x
				orderby x
				select x).Take(80).ToList();
			sb.AppendLine(text + "{");
			sb.AppendLine(text + "  \"label\": \"" + EscapeJson(label) + "\",");
			sb.AppendLine(text + "  \"isNull\": false,");
			sb.AppendLine(text + "  \"runtimeType\": \"" + EscapeJson(type.FullName) + "\",");
			sb.AppendLine(text + "  \"assembly\": \"" + EscapeJson(type.Assembly.GetName().Name) + "\",");
			WriteStringArray(sb, "properties", values2, indent + 2);
			sb.AppendLine(",");
			WriteStringArray(sb, "fields", values3, indent + 2);
			sb.AppendLine(",");
			WriteStringArray(sb, "methods", values, indent + 2);
			sb.AppendLine();
			sb.Append(text + "}");
		}

		private bool IsActiveExporterInstance()
		{
			if (m_instanceId != 0)
			{
				return m_instanceId == Volatile.Read(ref s_activeInstanceId);
			}
			return false;
		}

		private void OnEntityAddedFull(IEntity entity, EntityAddReason reason)
		{
			if (IsActiveExporterInstance() && m_machineExportCount < 200 && entity is Machine)
			{
				ExportWorldMachines(gameWasLoaded: true, "entityAddedFull:" + reason);
			}
		}

		private void ExportWorldMachines(bool gameWasLoaded, string trigger)
		{
			if (!IsActiveExporterInstance())
			{
				Log.Info("CLExporter: skipping export from inactive instance " + m_instanceId + ". Active instance is " + Volatile.Read(ref s_activeInstanceId) + ". Trigger=" + trigger);
				return;
			}
			lock (m_exportLock)
			{
				if (!IsActiveExporterInstance())
				{
					Log.Info("CLExporter: skipping locked export from inactive instance " + m_instanceId + ". Active instance is " + Volatile.Read(ref s_activeInstanceId) + ". Trigger=" + trigger);
				}
				else if (m_entitiesManager != null)
				{
					List<Machine> list = (from m in m_entitiesManager.GetAllEntitiesOfType<Machine>()
						orderby m.Prototype.Id.ToString(), m.Id.ToString()
						select m).ToList();
					m_machineExportCount++;
					string path = Path.Combine(base.Manifest.RootDirectoryPath, "coi_world_machines.json");
					string contents = BuildWorldMachinesJson(list, gameWasLoaded, trigger, m_machineExportCount);
					File.WriteAllText(path, contents);
					Log.Info("CLExporter: wrote world machines export. Trigger=" + trigger + ", Count=" + list.Count);
					string text = Path.Combine(base.Manifest.RootDirectoryPath, "coi_power.json");
					string contents2 = BuildPowerJson(m_electricityManager);
					File.WriteAllText(text, contents2);
					Log.Info("CLExporter: wrote power export to " + text);
					string text2 = Path.Combine(base.Manifest.RootDirectoryPath, "coi_power_entities.json");
					string text3 = Path.Combine(base.Manifest.RootDirectoryPath, "coi_power_asset_probe.json");
					string contents3 = BuildPowerAssetProbeJson(m_entitiesManager);
					File.WriteAllText(text3, contents3);
					Log.Info("CLExporter: wrote focused power asset probe to " + text3);
					string contents4 = BuildPowerEntitiesJson(m_entitiesManager);
					File.WriteAllText(text2, contents4);
					string text4 = Path.Combine(base.Manifest.RootDirectoryPath, "coi_entity_name_probe.json");
					string contents5 = BuildEntityNameProbeJson(m_entitiesManager);
					File.WriteAllText(text4, contents5);
					Log.Info("CLExporter: wrote entity name probe to " + text4);
					Log.Info("CLExporter: wrote power entities export to " + text2);
					if (trigger == "timer" || trigger == "initialize")
					{
						string text5 = Path.Combine(base.Manifest.RootDirectoryPath, "coi_machine_buffer_probe.json");
						string contents6 = BuildMachineBufferProbeJson(list);
						File.WriteAllText(text5, contents6);
						Log.Info("CLExporter: wrote machine buffer probe to " + text5);
						string text6 = Path.Combine(base.Manifest.RootDirectoryPath, "coi_power_farm_type_probe.json");
						string contents7 = BuildPowerFarmTypeProbeJson();
						File.WriteAllText(text6, contents7);
						Log.Info("CLExporter: wrote power/farm type probe to " + text6);
						string text7 = Path.Combine(base.Manifest.RootDirectoryPath, "coi_power_farm_live_probe.json");
						string contents8 = BuildPowerFarmLiveProbeJson(m_entitiesManager, m_resolver);
						string text8 = Path.Combine(base.Manifest.RootDirectoryPath, "coi_power_entity_probe.json");
						string contents9 = BuildPowerEntityProbeJson(m_entitiesManager);
						File.WriteAllText(text8, contents9);
						Log.Info("CLExporter: wrote power entity probe to " + text8);
						File.WriteAllText(text7, contents8);
						Log.Info("CLExporter: wrote power/farm live probe to " + text7);
					}
				}
			}
		}

		private void WriteWorldMachinesError(string trigger, Exception ex)
		{
			string text = Path.Combine(base.Manifest.RootDirectoryPath, "coi_world_machines.json");
			string contents = "{\n  \"exportType\": \"worldMachines\",\n  \"success\": false,\n  \"trigger\": \"" + EscapeJson(trigger) + "\",\n  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"\n}\n";
			File.WriteAllText(text, contents);
			Log.Info("CLExporter: wrote world machines error export to " + text);
		}

		private static List<string> GetAssignedRecipeIds(Machine machine)
		{
			var recipeIds = new List<string>();
			foreach (RecipeProto recipe in machine.RecipesAssigned)
			{
				recipeIds.Add(recipe.Id.Value);
			}
			return recipeIds;
		}

		private void StartRefreshTimer()
		{
			if (m_refreshTimer != null)
			{
				return;
			}
			m_refreshTimer = new System.Timers.Timer(10000.0);
			m_refreshTimer.AutoReset = true;
			m_refreshTimer.Elapsed += delegate
			{
				try
				{
					if (!IsActiveExporterInstance())
					{
						Log.Info("CLExporter: stopping inactive timer for instance " + m_instanceId + ". Active instance is " + Volatile.Read(ref s_activeInstanceId));
						try
						{
							m_refreshTimer.Stop();
							m_refreshTimer.Dispose();
						}
						catch
						{
						}
						m_refreshTimer = null;
					}
					else
					{
						ExportWorldMachines(gameWasLoaded: true, "timer");
					}
				}
				catch (Exception ex)
				{
					WriteWorldMachinesError("timer", ex);
				}
			};
			m_refreshTimer.Start();
			Log.Info("CLExporter: refresh timer started. IntervalMs=" + 10000.0);
		}

		private static string BuildMachineBufferProbeJson(List<Machine> machines)
		{
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("{");
			stringBuilder.AppendLine("  \"exportType\": \"machineBufferProbe\",");
			stringBuilder.AppendLine("  \"machineCount\": " + machines.Count + ",");
			stringBuilder.AppendLine("  \"machines\": [");
			int num = Math.Min(machines.Count, 8);
			for (int i = 0; i < num; i++)
			{
				Machine machine = machines[i];
				stringBuilder.AppendLine("    {");
				stringBuilder.AppendLine("      \"entityId\": \"" + EscapeJson(machine.Id.ToString()) + "\",");
				stringBuilder.AppendLine("      \"machineId\": \"" + EscapeJson(machine.Prototype.Id.ToString()) + "\",");
				stringBuilder.AppendLine("      \"title\": \"" + EscapeJson(machine.DefaultTitle.ToString()) + "\",");
				stringBuilder.AppendLine("      \"currentState\": \"" + EscapeJson(machine.CurrentState.ToString()) + "\",");
				stringBuilder.AppendLine("      \"utilization\": \"" + EscapeJson(machine.Utilization.ToString()) + "\",");
				stringBuilder.AppendLine("      \"privateFields\": {");
				WritePrivateFieldProbe(stringBuilder, machine, "m_inputBuffers", 8);
				stringBuilder.AppendLine(",");
				WritePrivateFieldProbe(stringBuilder, machine, "m_outputBuffers", 8);
				stringBuilder.AppendLine(",");
				WritePrivateFieldProbe(stringBuilder, machine, "m_productInNeedOfTransport", 8);
				stringBuilder.AppendLine(",");
				WritePrivateFieldProbe(stringBuilder, machine, "m_recipeResult", 8);
				stringBuilder.AppendLine();
				stringBuilder.AppendLine("      }");
				stringBuilder.Append("    }");
				if (i < num - 1)
				{
					stringBuilder.Append(",");
				}
				stringBuilder.AppendLine();
			}
			stringBuilder.AppendLine("  ]");
			stringBuilder.AppendLine("}");
			return stringBuilder.ToString();
		}

		private static void WritePrivateFieldProbe(StringBuilder sb, object target, string fieldName, int indent)
		{
			string text = new string(' ', indent);
			try
			{
				Type type = target.GetType();
				FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				sb.AppendLine(text + "\"" + EscapeJson(fieldName) + "\": {");
				if (field == null)
				{
					sb.AppendLine(text + "  \"found\": false");
					sb.Append(text + "}");
					return;
				}
				object value = field.GetValue(target);
				sb.AppendLine(text + "  \"found\": true,");
				sb.AppendLine(text + "  \"fieldType\": \"" + EscapeJson(field.FieldType.FullName) + "\",");
				if (value == null)
				{
					sb.AppendLine(text + "  \"isNull\": true");
					sb.Append(text + "}");
					return;
				}
				Type type2 = value.GetType();
				sb.AppendLine(text + "  \"isNull\": false,");
				sb.AppendLine(text + "  \"runtimeType\": \"" + EscapeJson(type2.FullName) + "\",");
				sb.AppendLine(text + "  \"toString\": \"" + EscapeJson(value.ToString()) + "\",");
				List<string> values = (from p in type2.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
					select p.PropertyType.FullName + " " + p.Name into x
					orderby x
					select x).Take(120).ToList();
				List<string> values2 = (from f in type2.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
					select f.FieldType.FullName + " " + f.Name into x
					orderby x
					select x).Take(120).ToList();
				List<string> values3 = (from x in (from m in type2.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
						select m.ReturnType.FullName + " " + m.Name + "(" + string.Join(", ", from p in m.GetParameters()
							select p.ParameterType.FullName + " " + p.Name) + ")").Distinct()
					orderby x
					select x).Take(160).ToList();
				WriteStringArray(sb, "properties", values, indent + 2);
				sb.AppendLine(",");
				WriteStringArray(sb, "fields", values2, indent + 2);
				sb.AppendLine(",");
				WriteStringArray(sb, "methods", values3, indent + 2);
				sb.AppendLine(",");
				WriteEnumerableItemsProbe(sb, value, "items", indent + 2);
				sb.AppendLine();
				sb.Append(text + "}");
			}
			catch (Exception ex)
			{
				sb.AppendLine(text + "\"" + EscapeJson(fieldName) + "\": {");
				sb.AppendLine(text + "  \"found\": false,");
				sb.AppendLine(text + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
				sb.Append(text + "}");
			}
		}

		private static void WriteEnumerableItemsProbe(StringBuilder sb, object value, string name, int indent)
		{
			string text = new string(' ', indent);
			List<string> list = new List<string>();
			if (value != null)
			{
				Type type = value.GetType();
				PropertyInfo property = type.GetProperty("Count", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				PropertyInfo propertyInfo = type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((PropertyInfo p) => p.Name == "Item" && p.GetIndexParameters().Length == 1);
				if (property != null && propertyInfo != null)
				{
					int num = Convert.ToInt32(property.GetValue(value, null));
					int num2 = Math.Min(num, 20);
					for (int num3 = 0; num3 < num2; num3++)
					{
						try
						{
							object value2 = propertyInfo.GetValue(value, new object[1] { num3 });
							list.Add(BuildObjectMiniProbeString(value2));
						}
						catch (Exception ex)
						{
							list.Add("{ \"index\": " + num3 + ", \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\" }");
						}
					}
					if (num > num2)
					{
						list.Add("{ \"truncated\": true }");
					}
				}
				else if (value is IEnumerable enumerable)
				{
					int num4 = 0;
					foreach (object item in enumerable)
					{
						if (num4 >= 20)
						{
							list.Add("{ \"truncated\": true }");
							break;
						}
						list.Add(BuildObjectMiniProbeString(item));
						num4++;
					}
				}
				else
				{
					list.Add("{ \"isEnumerable\": false, \"hasCountIndexer\": false }");
				}
			}
			else
			{
				list.Add("{ \"isNull\": true }");
			}
			sb.AppendLine(text + "\"" + EscapeJson(name) + "\": [");
			for (int num5 = 0; num5 < list.Count; num5++)
			{
				sb.Append(text + "  " + list[num5]);
				if (num5 < list.Count - 1)
				{
					sb.Append(",");
				}
				sb.AppendLine();
			}
			sb.Append(text + "]");
		}

		private static string BuildObjectMiniProbeString(object item)
		{
			if (item == null)
			{
				return "{ \"isNull\": true }";
			}
			try
			{
				Type type = item.GetType();
				StringBuilder stringBuilder = new StringBuilder();
				stringBuilder.Append("{ ");
				stringBuilder.Append("\"isNull\": false, ");
				stringBuilder.Append("\"runtimeType\": \"" + EscapeJson(type.FullName) + "\", ");
				stringBuilder.Append("\"toString\": \"" + EscapeJson(item.ToString()) + "\", ");
				stringBuilder.Append("\"properties\": [");
				List<PropertyInfo> list = (from p in type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
					orderby p.Name
					select p).Take(40).ToList();
				for (int num = 0; num < list.Count; num++)
				{
					PropertyInfo propertyInfo = list[num];
					stringBuilder.Append("{ ");
					stringBuilder.Append("\"name\": \"" + EscapeJson(propertyInfo.Name) + "\", ");
					stringBuilder.Append("\"type\": \"" + EscapeJson(propertyInfo.PropertyType.FullName) + "\"");
					if (propertyInfo.GetIndexParameters().Length == 0)
					{
						try
						{
							object value = propertyInfo.GetValue(item, null);
							stringBuilder.Append(", \"value\": \"" + EscapeJson((value == null) ? "" : value.ToString()) + "\"");
						}
						catch (Exception ex)
						{
							stringBuilder.Append(", \"valueError\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
						}
					}
					stringBuilder.Append(" }");
					if (num < list.Count - 1)
					{
						stringBuilder.Append(", ");
					}
				}
				stringBuilder.Append("], ");
				stringBuilder.Append("\"fields\": [");
				List<FieldInfo> list2 = (from f in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
					orderby f.Name
					select f).Take(40).ToList();
				for (int num2 = 0; num2 < list2.Count; num2++)
				{
					FieldInfo fieldInfo = list2[num2];
					stringBuilder.Append("{ ");
					stringBuilder.Append("\"name\": \"" + EscapeJson(fieldInfo.Name) + "\", ");
					stringBuilder.Append("\"type\": \"" + EscapeJson(fieldInfo.FieldType.FullName) + "\"");
					try
					{
						object value2 = fieldInfo.GetValue(item);
						stringBuilder.Append(", \"value\": \"" + EscapeJson((value2 == null) ? "" : value2.ToString()) + "\"");
					}
					catch (Exception ex2)
					{
						stringBuilder.Append(", \"valueError\": \"" + EscapeJson(ex2.GetType().Name + ": " + ex2.Message) + "\"");
					}
					stringBuilder.Append(" }");
					if (num2 < list2.Count - 1)
					{
						stringBuilder.Append(", ");
					}
				}
				stringBuilder.Append("]");
				stringBuilder.Append(" }");
				return stringBuilder.ToString();
			}
			catch (Exception ex3)
			{
				return "{ \"error\": \"" + EscapeJson(ex3.GetType().Name + ": " + ex3.Message) + "\" }";
			}
		}

		private static void WriteMachineBufferArray(StringBuilder sb, string jsonName, Machine machine, string privateFieldName, int indent)
		{
			string text = new string(' ', indent);
			sb.AppendLine(text + "\"" + EscapeJson(jsonName) + "\": [");
			List<object> machineBuffers = GetMachineBuffers(machine, privateFieldName);
			for (int i = 0; i < machineBuffers.Count; i++)
			{
				object obj = machineBuffers[i];
				sb.AppendLine(text + "  {");
				sb.AppendLine(text + "    \"productId\": \"" + EscapeJson(GetProductIdFromBuffer(obj)) + "\",");
				sb.AppendLine(text + "    \"quantity\": \"" + EscapeJson(GetPropertyString(obj, "Quantity")) + "\",");
				sb.AppendLine(text + "    \"capacity\": \"" + EscapeJson(GetPropertyString(obj, "Capacity")) + "\",");
				sb.AppendLine(text + "    \"usableCapacity\": \"" + EscapeJson(GetPropertyString(obj, "UsableCapacity")) + "\",");
				sb.AppendLine(text + "    \"isFull\": " + GetPropertyBoolJson(obj, "IsFull") + ",");
				sb.AppendLine(text + "    \"isEmpty\": " + GetPropertyBoolJson(obj, "IsEmpty") + ",");
				sb.AppendLine(text + "    \"isNotUsedByCurrentRecipes\": " + GetPropertyBoolJson(obj, "IsNotUsedByCurrentRecipes") + ",");
				sb.AppendLine(text + "    \"isAnyPortConnected\": " + GetPropertyBoolJson(obj, "IsAnyPortConnected") + ",");
				sb.AppendLine(text + "    \"isRegisteredToLogistics\": " + GetPropertyBoolJson(obj, "IsRegisteredToLogistics"));
				sb.Append(text + "  }");
				if (i < machineBuffers.Count - 1)
				{
					sb.Append(",");
				}
				sb.AppendLine();
			}
			sb.Append(text + "]");
		}

		private static List<object> GetMachineBuffers(Machine machine, string privateFieldName)
		{
			List<object> list = new List<object>();
			try
			{
				FieldInfo field = machine.GetType().GetField(privateFieldName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (field == null)
				{
					return list;
				}
				object value = field.GetValue(machine);
				if (value == null)
				{
					return list;
				}
				Type type = value.GetType();
				PropertyInfo property = type.GetProperty("Count", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				PropertyInfo propertyInfo = type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((PropertyInfo p) => p.Name == "Item" && p.GetIndexParameters().Length == 1);
				if (property == null || propertyInfo == null)
				{
					return list;
				}
				int num = Convert.ToInt32(property.GetValue(value, null));
				for (int num2 = 0; num2 < num; num2++)
				{
					object value2 = propertyInfo.GetValue(value, new object[1] { num2 });
					if (value2 != null)
					{
						list.Add(value2);
					}
				}
			}
			catch
			{
			}
			return list;
		}

		private static string GetProductIdFromBuffer(object buffer)
		{
			string propertyString = GetPropertyString(buffer, "Product");
			if (string.IsNullOrWhiteSpace(propertyString))
			{
				return "";
			}
			propertyString = propertyString.Trim();
			int num = propertyString.IndexOf(" ");
			if (num > 0)
			{
				return propertyString.Substring(0, num);
			}
			return propertyString;
		}

		private static string GetPropertyString(object target, string propertyName)
		{
			try
			{
				if (target == null)
				{
					return "";
				}
				PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (property == null || property.GetIndexParameters().Length != 0)
				{
					return "";
				}
				object value = property.GetValue(target, null);
				return (value == null) ? "" : value.ToString();
			}
			catch
			{
				return "";
			}
		}

		private static string GetPropertyBoolJson(object target, string propertyName)
		{
			string propertyString = GetPropertyString(target, propertyName);
			if (string.Equals(propertyString, "True", StringComparison.OrdinalIgnoreCase))
			{
				return "true";
			}
			string.Equals(propertyString, "False", StringComparison.OrdinalIgnoreCase);
			return "false";
		}

		private static string BuildPowerFarmLiveProbeJson(IEntitiesManager entitiesManager, DependencyResolver resolver)
		{
			string[] array = new string[7] { "Mafi.Core.Buildings.Farms.Farm", "Mafi.Core.Buildings.Farms.AnimalFarm", "Mafi.Base.Prototypes.Machines.PowerGenerators.ElectricityGeneratorFromProduct", "Mafi.Base.Prototypes.Machines.PowerGenerators.ElectricityGeneratorFromMechPower", "Mafi.Base.Prototypes.Machines.PowerGenerators.MechPowerGeneratorFromProduct", "Mafi.Base.Prototypes.Machines.PowerGenerators.FlyWheelEntity", "Mafi.Base.Prototypes.Machines.SolarElectricityGenerator" };
			string[] array2 = new string[4] { "Mafi.Core.Factory.ElectricPower.IElectricityManager", "Mafi.Core.Factory.ElectricPower.ElectricityManager", "Mafi.Core.Factory.ElectricPower.ISolarPanelsManager", "Mafi.Core.Factory.ElectricPower.SolarPanelsManager" };
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("{");
			stringBuilder.AppendLine("  \"exportType\": \"powerFarmLiveProbe\",");
			stringBuilder.AppendLine("  \"entityTypeProbes\": [");
			for (int i = 0; i < array.Length; i++)
			{
				WriteLiveEntityTypeProbe(stringBuilder, entitiesManager, array[i], 4);
				if (i < array.Length - 1)
				{
					stringBuilder.Append(",");
				}
				stringBuilder.AppendLine();
			}
			stringBuilder.AppendLine("  ],");
			stringBuilder.AppendLine("  \"managerResolveProbes\": [");
			for (int j = 0; j < array2.Length; j++)
			{
				WriteResolverTypeProbe(stringBuilder, resolver, array2[j], 4);
				if (j < array2.Length - 1)
				{
					stringBuilder.Append(",");
				}
				stringBuilder.AppendLine();
			}
			stringBuilder.AppendLine("  ]");
			stringBuilder.AppendLine("}");
			return stringBuilder.ToString();
		}

		private static void WriteLiveEntityTypeProbe(StringBuilder sb, IEntitiesManager entitiesManager, string typeName, int indent)
		{
			string text = new string(' ', indent);
			sb.AppendLine(text + "{");
			sb.AppendLine(text + "  \"typeName\": \"" + EscapeJson(typeName) + "\",");
			try
			{
				Type type = FindTypeByFullName(typeName);
				if (type == null)
				{
					sb.AppendLine(text + "  \"typeFound\": false");
					sb.Append(text + "}");
					return;
				}
				List<object> allEntitiesOfTypeReflective = GetAllEntitiesOfTypeReflective(entitiesManager, type);
				sb.AppendLine(text + "  \"typeFound\": true,");
				sb.AppendLine(text + "  \"assembly\": \"" + EscapeJson(type.Assembly.GetName().Name) + "\",");
				sb.AppendLine(text + "  \"entityCount\": " + allEntitiesOfTypeReflective.Count + ",");
				WriteTypeMemberSummary(sb, type, "typeMembers", indent + 2);
				sb.AppendLine(",");
				sb.AppendLine(text + "  \"sampleEntities\": [");
				int num = Math.Min(allEntitiesOfTypeReflective.Count, 5);
				for (int i = 0; i < num; i++)
				{
					sb.Append(text + "    " + BuildObjectMiniProbeString(allEntitiesOfTypeReflective[i]));
					if (i < num - 1)
					{
						sb.Append(",");
					}
					sb.AppendLine();
				}
				sb.AppendLine(text + "  ]");
				sb.Append(text + "}");
			}
			catch (Exception ex)
			{
				sb.AppendLine(text + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
				sb.Append(text + "}");
			}
		}

		private static List<object> GetAllEntitiesOfTypeReflective(IEntitiesManager entitiesManager, Type entityType)
		{
			List<object> result = new List<object>();
			try
			{
				MethodInfo methodInfo = entitiesManager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => m.Name == "GetAllEntitiesOfType" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
				if (methodInfo == null)
				{
					return result;
				}
				MethodInfo methodInfo2 = methodInfo.MakeGenericMethod(entityType);
				object value = methodInfo2.Invoke(entitiesManager, null);
				AddObjectsFromCollection(result, value);
			}
			catch
			{
			}
			return result;
		}

		private static void WriteResolverTypeProbe(StringBuilder sb, DependencyResolver resolver, string typeName, int indent)
		{
			string text = new string(' ', indent);
			sb.AppendLine(text + "{");
			sb.AppendLine(text + "  \"typeName\": \"" + EscapeJson(typeName) + "\",");
			try
			{
				Type type = FindTypeByFullName(typeName);
				if (type == null)
				{
					sb.AppendLine(text + "  \"typeFound\": false");
					sb.Append(text + "}");
					return;
				}
				object obj = ResolveTypeReflective(resolver, type);
				sb.AppendLine(text + "  \"typeFound\": true,");
				sb.AppendLine(text + "  \"resolved\": " + ((obj != null) ? "true" : "false") + ",");
				if (obj == null)
				{
					WriteTypeMemberSummary(sb, type, "typeMembers", indent + 2);
					sb.AppendLine();
					sb.Append(text + "}");
					return;
				}
				Type type2 = obj.GetType();
				sb.AppendLine(text + "  \"runtimeType\": \"" + EscapeJson(type2.FullName) + "\",");
				sb.AppendLine(text + "  \"toString\": \"" + EscapeJson(obj.ToString()) + "\",");
				WriteTypeMemberSummary(sb, type2, "runtimeMembers", indent + 2);
				sb.AppendLine(",");
				sb.AppendLine(text + "  \"objectProbe\": " + BuildObjectMiniProbeString(obj));
				sb.Append(text + "}");
			}
			catch (Exception ex)
			{
				sb.AppendLine(text + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
				sb.Append(text + "}");
			}
		}

		private static object ResolveTypeReflective(DependencyResolver resolver, Type type)
		{
			try
			{
				MethodInfo methodInfo = resolver.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) => m.Name == "Resolve" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
				if (methodInfo == null)
				{
					return null;
				}
				MethodInfo methodInfo2 = methodInfo.MakeGenericMethod(type);
				return methodInfo2.Invoke(resolver, null);
			}
			catch
			{
				return null;
			}
		}

		private static Type FindTypeByFullName(string fullName)
		{
			return AppDomain.CurrentDomain.GetAssemblies().SelectMany(delegate(Assembly a)
			{
				try
				{
					return a.GetTypes();
				}
				catch
				{
					return Array.Empty<Type>();
				}
			}).FirstOrDefault((Type t) => t.FullName == fullName);
		}

		private static void AddObjectsFromCollection(List<object> result, object value)
		{
			if (value == null)
			{
				return;
			}
			Type type = value.GetType();
			PropertyInfo property = type.GetProperty("Count", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			PropertyInfo propertyInfo = type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((PropertyInfo p) => p.Name == "Item" && p.GetIndexParameters().Length == 1);
			if (property != null && propertyInfo != null)
			{
				int num = Convert.ToInt32(property.GetValue(value, null));
				for (int num2 = 0; num2 < num; num2++)
				{
					object value2 = propertyInfo.GetValue(value, new object[1] { num2 });
					if (value2 != null)
					{
						result.Add(value2);
					}
				}
			}
			else
			{
				if (!(value is IEnumerable enumerable))
				{
					return;
				}
				foreach (object item in enumerable)
				{
					if (item != null)
					{
						result.Add(item);
					}
				}
			}
		}

		private static void WriteTypeMemberSummary(StringBuilder sb, Type type, string jsonName, int indent)
		{
			string text = new string(' ', indent);
			sb.AppendLine(text + "\"" + EscapeJson(jsonName) + "\": {");
			sb.AppendLine(text + "  \"baseType\": \"" + EscapeJson((type.BaseType == null) ? "" : type.BaseType.FullName) + "\",");
			List<string> values = (from p in type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				select p.PropertyType.FullName + " " + p.Name into x
				orderby x
				select x).Take(120).ToList();
			List<string> values2 = (from f in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				select f.FieldType.FullName + " " + f.Name into x
				orderby x
				select x).Take(120).ToList();
			List<string> values3 = (from x in (from m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
					select m.ReturnType.FullName + " " + m.Name + "(" + string.Join(", ", from p in m.GetParameters()
						select p.ParameterType.FullName + " " + p.Name) + ")").Distinct()
				orderby x
				select x).Take(160).ToList();
			WriteStringArray(sb, "properties", values, indent + 2);
			sb.AppendLine(",");
			WriteStringArray(sb, "fields", values2, indent + 2);
			sb.AppendLine(",");
			WriteStringArray(sb, "methods", values3, indent + 2);
			sb.AppendLine();
			sb.Append(text + "}");
		}

		private static string BuildPowerJson(object electricityManager)
		{
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("{");
			stringBuilder.AppendLine("  \"exportType\": \"power\",");
			stringBuilder.AppendLine("  \"success\": " + ((electricityManager != null) ? "true" : "false") + ",");
			if (electricityManager == null)
			{
				stringBuilder.AppendLine("  \"error\": \"IElectricityManager was not resolved\"");
				stringBuilder.AppendLine("}");
				return stringBuilder.ToString();
			}
			stringBuilder.AppendLine("  \"runtimeType\": \"" + EscapeJson(electricityManager.GetType().FullName) + "\",");
			stringBuilder.AppendLine("  \"consumedThisTick\": \"" + EscapeJson(GetPropertyString(electricityManager, "ConsumedThisTick")) + "\",");
			stringBuilder.AppendLine("  \"demandedThisTick\": \"" + EscapeJson(GetPropertyString(electricityManager, "DemandedThisTick")) + "\",");
			stringBuilder.AppendLine("  \"generatedThisTick\": \"" + EscapeJson(GetPropertyString(electricityManager, "GeneratedThisTick")) + "\",");
			stringBuilder.AppendLine("  \"generationCapacityThisTick\": \"" + EscapeJson(GetPropertyString(electricityManager, "GenerationCapacityThisTick")) + "\",");
			stringBuilder.AppendLine("  \"maxGenerationCapacity\": \"" + EscapeJson(GetPropertyString(electricityManager, "MaxGenerationCapacity")) + "\",");
			stringBuilder.AppendLine("  \"wastedElectricityThisTick\": \"" + EscapeJson(GetPropertyString(electricityManager, "WastedElectricityThisTick")) + "\",");
			stringBuilder.AppendLine("  \"generatorsCount\": \"" + EscapeJson(GetPropertyString(electricityManager, "GeneratorsCount")) + "\",");
			stringBuilder.AppendLine("  \"consumersCount\": \"" + EscapeJson(GetPropertyString(electricityManager, "ConsumersCount")) + "\"");
			stringBuilder.AppendLine("}");
			return stringBuilder.ToString();
		}

		private static string BuildPowerEntityProbeJson(IEntitiesManager entitiesManager)
		{
			string[] array = new string[5] { "Mafi.Base.Prototypes.Machines.PowerGenerators.ElectricityGeneratorFromProduct", "Mafi.Base.Prototypes.Machines.PowerGenerators.ElectricityGeneratorFromMechPower", "Mafi.Base.Prototypes.Machines.PowerGenerators.MechPowerGeneratorFromProduct", "Mafi.Base.Prototypes.Machines.PowerGenerators.FlyWheelEntity", "Mafi.Base.Prototypes.Machines.SolarElectricityGenerator" };
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("{");
			stringBuilder.AppendLine("  \"exportType\": \"powerEntityProbe\",");
			stringBuilder.AppendLine("  \"entityTypeProbes\": [");
			for (int i = 0; i < array.Length; i++)
			{
				WritePowerEntityTypeProbe(stringBuilder, entitiesManager, array[i], 4);
				if (i < array.Length - 1)
				{
					stringBuilder.Append(",");
				}
				stringBuilder.AppendLine();
			}
			stringBuilder.AppendLine("  ]");
			stringBuilder.AppendLine("}");
			return stringBuilder.ToString();
		}

		private static void WritePowerEntityTypeProbe(StringBuilder sb, IEntitiesManager entitiesManager, string typeName, int indent)
		{
			string text = new string(' ', indent);
			sb.AppendLine(text + "{");
			sb.AppendLine(text + "  \"typeName\": \"" + EscapeJson(typeName) + "\",");
			try
			{
				Type type = FindTypeByFullName(typeName);
				if (type == null)
				{
					sb.AppendLine(text + "  \"typeFound\": false");
					sb.Append(text + "}");
					return;
				}
				List<object> allEntitiesOfTypeReflective = GetAllEntitiesOfTypeReflective(entitiesManager, type);
				sb.AppendLine(text + "  \"typeFound\": true,");
				sb.AppendLine(text + "  \"assembly\": \"" + EscapeJson(type.Assembly.GetName().Name) + "\",");
				sb.AppendLine(text + "  \"entityCount\": " + allEntitiesOfTypeReflective.Count + ",");
				WriteTypeMemberSummary(sb, type, "typeMembers", indent + 2);
				sb.AppendLine(",");
				sb.AppendLine(text + "  \"sampleEntities\": [");
				int num = Math.Min(allEntitiesOfTypeReflective.Count, 8);
				for (int i = 0; i < num; i++)
				{
					object entity = allEntitiesOfTypeReflective[i];
					WritePowerEntityObjectProbe(sb, entity, indent + 4);
					if (i < num - 1)
					{
						sb.Append(",");
					}
					sb.AppendLine();
				}
				sb.AppendLine(text + "  ]");
				sb.Append(text + "}");
			}
			catch (Exception ex)
			{
				sb.AppendLine(text + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
				sb.Append(text + "}");
			}
		}

		private static void WritePowerEntityObjectProbe(StringBuilder sb, object entity, int indent)
		{
			string text = new string(' ', indent);
			sb.AppendLine(text + "{");
			if (entity == null)
			{
				sb.AppendLine(text + "  \"isNull\": true");
				sb.Append(text + "}");
				return;
			}
			try
			{
				Type type = entity.GetType();
				sb.AppendLine(text + "  \"isNull\": false,");
				sb.AppendLine(text + "  \"runtimeType\": \"" + EscapeJson(type.FullName) + "\",");
				sb.AppendLine(text + "  \"toString\": \"" + EscapeJson(entity.ToString()) + "\",");
				WriteSimplePropertyValue(sb, entity, "Id", indent + 2);
				sb.AppendLine(",");
				WriteSimplePropertyValue(sb, entity, "DefaultTitle", indent + 2);
				sb.AppendLine(",");
				WriteSimplePropertyValue(sb, entity, "CurrentState", indent + 2);
				sb.AppendLine(",");
				WriteSimplePropertyValue(sb, entity, "State", indent + 2);
				sb.AppendLine(",");
				WriteSimplePropertyValue(sb, entity, "IsEnabled", indent + 2);
				sb.AppendLine(",");
				WriteSimplePropertyValue(sb, entity, "IsPaused", indent + 2);
				sb.AppendLine(",");
				WriteSimplePropertyValue(sb, entity, "IsEnabledNow", indent + 2);
				sb.AppendLine(",");
				WriteSimplePropertyValue(sb, entity, "Position3f", indent + 2);
				sb.AppendLine(",");
				WriteSimplePropertyValue(sb, entity, "Prototype", indent + 2);
				sb.AppendLine(",");
				WriteSimplePropertyValue(sb, entity, "ElectricityGenerator", indent + 2);
				sb.AppendLine(",");
				WriteSimplePropertyValue(sb, entity, "ElectricityConsumer", indent + 2);
				sb.AppendLine(",");
				WriteSimplePropertyValue(sb, entity, "Generator", indent + 2);
				sb.AppendLine(",");
				WriteSimplePropertyValue(sb, entity, "Consumer", indent + 2);
				sb.AppendLine(",");
				sb.AppendLine(text + "  \"privateFieldProbes\": {");
				string[] array = new string[12]
				{
					"m_electricityGenerator", "m_electricityConsumer", "m_generator", "m_consumer", "m_powerGenerator", "m_powerConsumer", "m_inputBuffers", "m_outputBuffers", "m_productBuffer", "m_productConsumer",
					"m_state", "m_proto"
				};
				for (int i = 0; i < array.Length; i++)
				{
					WritePrivateFieldProbe(sb, entity, array[i], indent + 4);
					if (i < array.Length - 1)
					{
						sb.Append(",");
					}
					sb.AppendLine();
				}
				sb.AppendLine(text + "  },");
				sb.AppendLine(text + "  \"publicBufferCollections\": {");
				WritePropertyObjectProbe(sb, entity, "InputBuffers", indent + 4);
				sb.AppendLine(",");
				WritePropertyObjectProbe(sb, entity, "OutputBuffers", indent + 4);
				sb.AppendLine();
				sb.AppendLine(text + "  }");
				sb.Append(text + "}");
			}
			catch (Exception ex)
			{
				sb.AppendLine(text + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
				sb.Append(text + "}");
			}
		}

		private static void WriteSimplePropertyValue(StringBuilder sb, object target, string propertyName, int indent)
		{
			string text = new string(' ', indent);
			try
			{
				if (target == null)
				{
					sb.Append(text + "\"" + EscapeJson(propertyName) + "\": { \"found\": false, \"isNull\": true }");
					return;
				}
				PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (property == null || property.GetIndexParameters().Length != 0)
				{
					sb.Append(text + "\"" + EscapeJson(propertyName) + "\": { \"found\": false }");
					return;
				}
				object value = property.GetValue(target, null);
				sb.Append(text + "\"" + EscapeJson(propertyName) + "\": { ");
				sb.Append("\"found\": true, ");
				sb.Append("\"type\": \"" + EscapeJson(property.PropertyType.FullName) + "\", ");
				sb.Append("\"value\": \"" + EscapeJson((value == null) ? "" : value.ToString()) + "\"");
				sb.Append(" }");
			}
			catch (Exception ex)
			{
				sb.Append(text + "\"" + EscapeJson(propertyName) + "\": { ");
				sb.Append("\"found\": false, ");
				sb.Append("\"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
				sb.Append(" }");
			}
		}

		private static void WritePropertyObjectProbe(StringBuilder sb, object target, string propertyName, int indent)
		{
			string text = new string(' ', indent);
			sb.AppendLine(text + "\"" + EscapeJson(propertyName) + "\": {");
			try
			{
				if (target == null)
				{
					sb.AppendLine(text + "  \"found\": false,");
					sb.AppendLine(text + "  \"isNull\": true");
					sb.Append(text + "}");
					return;
				}
				PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (property == null || property.GetIndexParameters().Length != 0)
				{
					sb.AppendLine(text + "  \"found\": false");
					sb.Append(text + "}");
					return;
				}
				object value = property.GetValue(target, null);
				sb.AppendLine(text + "  \"found\": true,");
				sb.AppendLine(text + "  \"propertyType\": \"" + EscapeJson(property.PropertyType.FullName) + "\",");
				if (value == null)
				{
					sb.AppendLine(text + "  \"isNull\": true");
					sb.Append(text + "}");
					return;
				}
				sb.AppendLine(text + "  \"isNull\": false,");
				sb.AppendLine(text + "  \"runtimeType\": \"" + EscapeJson(value.GetType().FullName) + "\",");
				sb.AppendLine(text + "  \"toString\": \"" + EscapeJson(value.ToString()) + "\",");
				WriteEnumerableItemsProbe(sb, value, "items", indent + 2);
				sb.AppendLine();
				sb.Append(text + "}");
			}
			catch (Exception ex)
			{
				sb.AppendLine(text + "  \"found\": false,");
				sb.AppendLine(text + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
				sb.Append(text + "}");
			}
		}

		private static string BuildPowerEntitiesJson(IEntitiesManager entitiesManager)
		{
			string[] array = new string[5] { "Mafi.Base.Prototypes.Machines.PowerGenerators.ElectricityGeneratorFromProduct", "Mafi.Base.Prototypes.Machines.PowerGenerators.ElectricityGeneratorFromMechPower", "Mafi.Base.Prototypes.Machines.PowerGenerators.MechPowerGeneratorFromProduct", "Mafi.Base.Prototypes.Machines.PowerGenerators.FlyWheelEntity", "Mafi.Base.Prototypes.Machines.SolarElectricityGenerator" };
			List<object> list = new List<object>();
			string[] array2 = array;
			foreach (string fullName in array2)
			{
				Type type = FindTypeByFullName(fullName);
				if (type == null)
				{
					continue;
				}
				List<object> allEntitiesOfTypeReflective = GetAllEntitiesOfTypeReflective(entitiesManager, type);
				foreach (object item in allEntitiesOfTypeReflective)
				{
					if (item != null)
					{
						list.Add(item);
					}
				}
			}
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("{");
			stringBuilder.AppendLine("  \"exportType\": \"powerEntities\",");
			stringBuilder.AppendLine("  \"success\": true,");
			stringBuilder.AppendLine("  \"generatorCount\": " + list.Count + ",");
			stringBuilder.AppendLine("  \"generators\": [");
			for (int j = 0; j < list.Count; j++)
			{
				WritePowerGeneratorJson(stringBuilder, list[j], 4);
				if (j < list.Count - 1)
				{
					stringBuilder.Append(",");
				}
				stringBuilder.AppendLine();
			}
			stringBuilder.AppendLine("  ]");
			stringBuilder.AppendLine("}");
			return stringBuilder.ToString();
		}

		private static void WritePowerGeneratorJson(StringBuilder sb, object entity, int indent)
		{
			string text = new string(' ', indent);
			Type type = entity.GetType();
			string propertyString = GetPropertyString(entity, "Id");
			string propertyString2 = GetPropertyString(entity, "DefaultTitle");
			string propertyString3 = GetPropertyString(entity, "CustomTitle");
			string text2 = CleanOptionString(propertyString3);
			string value = (string.IsNullOrWhiteSpace(text2) ? propertyString2 : text2);
			string propertyString4 = GetPropertyString(entity, "Position3f");
			string propertyBoolJson = GetPropertyBoolJson(entity, "IsEnabled");
			string propertyBoolJson2 = GetPropertyBoolJson(entity, "IsPaused");
			string propertyBoolJson3 = GetPropertyBoolJson(entity, "IsEnabledNow");
			string value2 = GetPropertyString(entity, "CurrentState");
			string propertyString5 = GetPropertyString(entity, "State");
			if (string.IsNullOrWhiteSpace(value2))
			{
				value2 = propertyString5;
			}
			string propertyString6 = GetPropertyString(entity, "MaxGenerationCapacity");
			string propertyString7 = GetPropertyString(entity, "GeneratedElectricityThisTick");
			string propertyString8 = GetPropertyString(entity, "UsedMechPowerThisTick");
			string propertyString9 = GetPropertyString(entity, "MaxOutputElectricity");
			string propertyString10 = GetPropertyString(entity, "PowerGeneratedLastTick");
			string propertyString11 = GetPropertyString(entity, "InputBufferQuantity");
			string propertyString12 = GetPropertyString(entity, "OutputBufferQuantity");
			string propertyString13 = GetPropertyString(entity, "CurrentFuelUsage");
			string propertyString14 = GetPropertyString(entity, "CurrentProductLeft");
			string propertyString15 = GetPropertyString(entity, "CurrentProgress");
			object privateFieldValue = GetPrivateFieldValue(entity, "m_proto");
			string value3 = "";
			string value4 = "";
			string value5 = "";
			string value6 = "";
			string value7 = "";
			string value8 = "";
			string value9 = "";
			string value10 = "";
			string value11 = "";
			string value12 = "";
			if (privateFieldValue != null)
			{
				value3 = GetPropertyString(privateFieldValue, "Id");
				value4 = GetPropertyString(privateFieldValue, "OutputElectricity");
				value5 = GetPropertyString(privateFieldValue, "Duration");
				value6 = GetFieldString(privateFieldValue, "InputProduct");
				value7 = GetFieldString(privateFieldValue, "OutputProduct");
				value8 = GetPropertyString(privateFieldValue, "ElectricityProduced");
				if (string.IsNullOrWhiteSpace(value8))
				{
					value8 = GetFieldString(privateFieldValue, "OutputElectricity");
				}
				value9 = GetFieldString(privateFieldValue, "ConsumedProduct");
				value10 = GetFieldString(privateFieldValue, "ProducedProduct");
				value11 = GetFieldString(privateFieldValue, "MechPowerOutput");
				value12 = GetFieldString(privateFieldValue, "RecipeDuration");
			}
			object propertyValue = GetPropertyValue(entity, "InputBuffer");
			object propertyValue2 = GetPropertyValue(entity, "OutputBuffer");
			sb.AppendLine(text + "{");
			sb.AppendLine(text + "  \"entityId\": \"" + EscapeJson(propertyString) + "\",");
			sb.AppendLine(text + "  \"title\": \"" + EscapeJson(propertyString2) + "\",");
			sb.AppendLine(text + "  \"customTitleRaw\": \"" + EscapeJson(propertyString3) + "\",");
			sb.AppendLine(text + "  \"customTitle\": \"" + EscapeJson(text2) + "\",");
			sb.AppendLine(text + "  \"displayTitle\": \"" + EscapeJson(value) + "\",");
			sb.AppendLine(text + "  \"runtimeType\": \"" + EscapeJson(type.FullName) + "\",");
			sb.AppendLine(text + "  \"shortType\": \"" + EscapeJson(type.Name) + "\",");
			sb.AppendLine(text + "  \"prototypeId\": \"" + EscapeJson(value3) + "\",");
			sb.AppendLine(text + "  \"position\": \"" + EscapeJson(propertyString4) + "\",");
			sb.AppendLine(text + "  \"currentState\": \"" + EscapeJson(value2) + "\",");
			sb.AppendLine(text + "  \"isEnabled\": " + propertyBoolJson + ",");
			sb.AppendLine(text + "  \"isPaused\": " + propertyBoolJson2 + ",");
			sb.AppendLine(text + "  \"isEnabledNow\": " + propertyBoolJson3 + ",");
			sb.AppendLine(text + "  \"maxGenerationCapacity\": \"" + EscapeJson(propertyString6) + "\",");
			sb.AppendLine(text + "  \"generatedElectricityThisTick\": \"" + EscapeJson(propertyString7) + "\",");
			sb.AppendLine(text + "  \"usedMechPowerThisTick\": \"" + EscapeJson(propertyString8) + "\",");
			sb.AppendLine(text + "  \"maxOutputElectricity\": \"" + EscapeJson(propertyString9) + "\",");
			sb.AppendLine(text + "  \"electricityProduced\": \"" + EscapeJson(value8) + "\",");
			sb.AppendLine(text + "  \"powerGeneratedLastTick\": \"" + EscapeJson(propertyString10) + "\",");
			sb.AppendLine(text + "  \"inputBufferQuantity\": \"" + EscapeJson(propertyString11) + "\",");
			sb.AppendLine(text + "  \"outputBufferQuantity\": \"" + EscapeJson(propertyString12) + "\",");
			sb.AppendLine(text + "  \"consumedProductRaw\": \"" + EscapeJson(value9) + "\",");
			sb.AppendLine(text + "  \"producedProductRaw\": \"" + EscapeJson(value10) + "\",");
			sb.AppendLine(text + "  \"mechPowerOutput\": \"" + EscapeJson(value11) + "\",");
			sb.AppendLine(text + "  \"recipeDuration\": \"" + EscapeJson(value12) + "\",");
			sb.AppendLine(text + "  \"currentFuelUsage\": \"" + EscapeJson(propertyString13) + "\",");
			sb.AppendLine(text + "  \"currentProductLeft\": \"" + EscapeJson(propertyString14) + "\",");
			sb.AppendLine(text + "  \"currentProgress\": \"" + EscapeJson(propertyString15) + "\",");
			sb.AppendLine(text + "  \"outputElectricity\": \"" + EscapeJson(value4) + "\",");
			sb.AppendLine(text + "  \"duration\": \"" + EscapeJson(value5) + "\",");
			sb.AppendLine(text + "  \"inputProductRaw\": \"" + EscapeJson(value6) + "\",");
			sb.AppendLine(text + "  \"outputProductRaw\": \"" + EscapeJson(value7) + "\",");
			sb.AppendLine(text + "  \"inputBuffer\": ");
			WriteProductBufferJson(sb, propertyValue, indent + 2);
			sb.AppendLine(",");
			sb.AppendLine(text + "  \"outputBuffer\": ");
			WriteOptionProductBufferJson(sb, propertyValue2, indent + 2);
			sb.AppendLine();
			sb.Append(text + "}");
		}

		private static object GetPropertyValue(object target, string propertyName)
		{
			try
			{
				if (target == null)
				{
					return null;
				}
				PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (property == null || property.GetIndexParameters().Length != 0)
				{
					return null;
				}
				return property.GetValue(target, null);
			}
			catch
			{
				return null;
			}
		}

		private static object GetPrivateFieldValue(object target, string fieldName)
		{
			try
			{
				if (target == null)
				{
					return null;
				}
				FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (field == null)
				{
					return null;
				}
				return field.GetValue(target);
			}
			catch
			{
				return null;
			}
		}

		private static string GetFieldString(object target, string fieldName)
		{
			try
			{
				object privateFieldValue = GetPrivateFieldValue(target, fieldName);
				return (privateFieldValue == null) ? "" : privateFieldValue.ToString();
			}
			catch
			{
				return "";
			}
		}

		private static void WriteProductBufferJson(StringBuilder sb, object buffer, int indent)
		{
			string text = new string(' ', indent);
			if (buffer == null)
			{
				sb.AppendLine(text + "null");
				return;
			}
			string propertyString = GetPropertyString(buffer, "Product");
			string value = ExtractProductId(propertyString);
			sb.AppendLine(text + "{");
			sb.AppendLine(text + "  \"productId\": \"" + EscapeJson(value) + "\",");
			sb.AppendLine(text + "  \"productRaw\": \"" + EscapeJson(propertyString) + "\",");
			sb.AppendLine(text + "  \"quantity\": \"" + EscapeJson(GetPropertyString(buffer, "Quantity")) + "\",");
			sb.AppendLine(text + "  \"capacity\": \"" + EscapeJson(GetPropertyString(buffer, "Capacity")) + "\",");
			sb.AppendLine(text + "  \"usableCapacity\": \"" + EscapeJson(GetPropertyString(buffer, "UsableCapacity")) + "\",");
			sb.AppendLine(text + "  \"isFull\": " + GetPropertyBoolJson(buffer, "IsFull") + ",");
			sb.AppendLine(text + "  \"isEmpty\": " + GetPropertyBoolJson(buffer, "IsEmpty") + ",");
			sb.AppendLine(text + "  \"isAnyPortConnected\": " + GetPropertyBoolJson(buffer, "IsAnyPortConnected") + ",");
			sb.AppendLine(text + "  \"isRegisteredToLogistics\": " + GetPropertyBoolJson(buffer, "IsRegisteredToLogistics"));
			sb.Append(text + "}");
		}

		private static void WriteOptionProductBufferJson(StringBuilder sb, object optionValue, int indent)
		{
			string text = new string(' ', indent);
			if (optionValue == null)
			{
				sb.AppendLine(text + "null");
				return;
			}
			string text2 = optionValue.ToString();
			if (string.IsNullOrWhiteSpace(text2) || text2.StartsWith("None:"))
			{
				sb.AppendLine(text + "null");
				return;
			}
			object obj = TryGetOptionValue(optionValue);
			if (obj == null)
			{
				sb.AppendLine(text + "{");
				sb.AppendLine(text + "  \"optionRaw\": \"" + EscapeJson(text2) + "\"");
				sb.Append(text + "}");
			}
			else
			{
				WriteProductBufferJson(sb, obj, indent);
			}
		}

		private static object TryGetOptionValue(object optionValue)
		{
			try
			{
				if (optionValue == null)
				{
					return null;
				}
				List<PropertyInfo> source = (from p in optionValue.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
					where p.GetIndexParameters().Length == 0
					select p).ToList();
				string[] array = new string[3] { "Value", "ValueOrDefault", "Data" };
				foreach (string name in array)
				{
					PropertyInfo propertyInfo = source.FirstOrDefault((PropertyInfo p) => p.Name == name);
					if (propertyInfo != null)
					{
						object value = propertyInfo.GetValue(optionValue, null);
						if (value != null)
						{
							return value;
						}
					}
				}
				FieldInfo[] fields = optionValue.GetType().GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				string[] array2 = new string[3] { "m_value", "value", "_value" };
				foreach (string name2 in array2)
				{
					FieldInfo fieldInfo = fields.FirstOrDefault((FieldInfo f) => f.Name == name2);
					if (fieldInfo != null)
					{
						object value2 = fieldInfo.GetValue(optionValue);
						if (value2 != null)
						{
							return value2;
						}
					}
				}
			}
			catch
			{
			}
			return null;
		}

		private static string ExtractProductId(string productRaw)
		{
			if (string.IsNullOrWhiteSpace(productRaw))
			{
				return "";
			}
			string text = productRaw.Trim();
			int num = text.IndexOf(" ");
			if (num > 0)
			{
				return text.Substring(0, num);
			}
			return text;
		}

		private static string BuildPowerAssetProbeJson(IEntitiesManager entitiesManager)
		{
			string[] array = new string[3] { "Mafi.Base.Prototypes.Machines.SolarElectricityGenerator", "Mafi.Base.Prototypes.Machines.PowerGenerators.MechPowerGeneratorFromProduct", "Mafi.Base.Prototypes.Machines.PowerGenerators.ElectricityGeneratorFromMechPower" };
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("{");
			stringBuilder.AppendLine("  \"exportType\": \"powerAssetProbe\",");
			stringBuilder.AppendLine("  \"purpose\": \"Focused probe for solar generation, turbine steam/input/output data, and mechanical power chain data.\",");
			stringBuilder.AppendLine("  \"types\": [");
			for (int i = 0; i < array.Length; i++)
			{
				WriteFocusedPowerAssetTypeProbe(stringBuilder, entitiesManager, array[i], 4);
				if (i < array.Length - 1)
				{
					stringBuilder.Append(",");
				}
				stringBuilder.AppendLine();
			}
			stringBuilder.AppendLine("  ]");
			stringBuilder.AppendLine("}");
			return stringBuilder.ToString();
		}

		private static void WriteFocusedPowerAssetTypeProbe(StringBuilder sb, IEntitiesManager entitiesManager, string typeName, int indent)
		{
			string text = new string(' ', indent);
			sb.AppendLine(text + "{");
			sb.AppendLine(text + "  \"typeName\": \"" + EscapeJson(typeName) + "\",");
			try
			{
				Type type = FindTypeByFullName(typeName);
				if (type == null)
				{
					sb.AppendLine(text + "  \"typeFound\": false");
					sb.Append(text + "}");
					return;
				}
				List<object> allEntitiesOfTypeReflective = GetAllEntitiesOfTypeReflective(entitiesManager, type);
				sb.AppendLine(text + "  \"typeFound\": true,");
				sb.AppendLine(text + "  \"assembly\": \"" + EscapeJson(type.Assembly.GetName().Name) + "\",");
				sb.AppendLine(text + "  \"entityCount\": " + allEntitiesOfTypeReflective.Count + ",");
				WriteFocusedMemberNameScan(sb, type, "interestingMembers", indent + 2);
				sb.AppendLine(",");
				sb.AppendLine(text + "  \"samples\": [");
				int num = Math.Min(allEntitiesOfTypeReflective.Count, 10);
				for (int i = 0; i < num; i++)
				{
					WriteFocusedPowerAssetObjectProbe(sb, allEntitiesOfTypeReflective[i], indent + 4);
					if (i < num - 1)
					{
						sb.Append(",");
					}
					sb.AppendLine();
				}
				sb.AppendLine(text + "  ]");
				sb.Append(text + "}");
			}
			catch (Exception ex)
			{
				sb.AppendLine(text + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
				sb.Append(text + "}");
			}
		}

		private static void WriteFocusedPowerAssetObjectProbe(StringBuilder sb, object entity, int indent)
		{
			string text = new string(' ', indent);
			sb.AppendLine(text + "{");
			if (entity == null)
			{
				sb.AppendLine(text + "  \"isNull\": true");
				sb.Append(text + "}");
				return;
			}
			try
			{
				Type type = entity.GetType();
				sb.AppendLine(text + "  \"runtimeType\": \"" + EscapeJson(type.FullName) + "\",");
				sb.AppendLine(text + "  \"toString\": \"" + EscapeJson(entity.ToString()) + "\",");
				string[] array = new string[36]
				{
					"Id", "DefaultTitle", "CurrentState", "State", "IsEnabled", "IsPaused", "IsEnabledNow", "Position3f", "MaxGenerationCapacity", "GenerationCapacity",
					"CurrentGeneration", "CurrentGeneratedElectricity", "GeneratedElectricityThisTick", "GeneratedPowerThisTick", "OutputElectricity", "CurrentOutputElectricity", "GeneratedMechPowerThisTick", "ProducedMechPowerThisTick", "CurrentMechPower", "UsedMechPowerThisTick",
					"MaxMechPower", "InputBuffer", "OutputBuffer", "InputBuffers", "OutputBuffers", "CurrentRecipe", "Recipe", "SelectedRecipe", "Prototype", "Proto",
					"ElectricityGenerator", "ElectricityConsumer", "MechPowerGenerator", "MechanicalPowerGenerator", "ProductConsumer", "ProductProducer"
				};
				sb.AppendLine(text + "  \"directProperties\": {");
				for (int i = 0; i < array.Length; i++)
				{
					WriteFocusedPropertyProbe(sb, entity, array[i], indent + 4);
					if (i < array.Length - 1)
					{
						sb.Append(",");
					}
					sb.AppendLine();
				}
				sb.AppendLine(text + "  },");
				string[] array2 = new string[26]
				{
					"m_proto", "m_state", "m_currentState", "m_electricityGenerator", "m_electricityConsumer", "m_generator", "m_consumer", "m_mechPowerGenerator", "m_mechanicalPowerGenerator", "m_powerGenerator",
					"m_powerConsumer", "m_productConsumer", "m_productProducer", "m_inputBuffer", "m_outputBuffer", "m_inputBuffers", "m_outputBuffers", "m_productBuffer", "m_currentRecipe", "m_recipe",
					"m_selectedRecipe", "m_maxGenerationCapacity", "m_generatedElectricityThisTick", "m_generatedMechPowerThisTick", "m_producedMechPowerThisTick", "m_usedMechPowerThisTick"
				};
				sb.AppendLine(text + "  \"privateFields\": {");
				for (int j = 0; j < array2.Length; j++)
				{
					WriteFocusedFieldProbe(sb, entity, array2[j], indent + 4);
					if (j < array2.Length - 1)
					{
						sb.Append(",");
					}
					sb.AppendLine();
				}
				sb.AppendLine(text + "  },");
				object value = GetPrivateFieldValue(entity, "m_proto") ?? GetPropertyValue(entity, "Prototype") ?? GetPropertyValue(entity, "Proto");
				sb.AppendLine(text + "  \"prototypeProbe\": ");
				WriteFocusedNestedObjectProbe(sb, value, indent + 2);
				sb.AppendLine(",");
				object value2 = GetPropertyValue(entity, "ElectricityGenerator") ?? GetPrivateFieldValue(entity, "m_electricityGenerator");
				sb.AppendLine(text + "  \"electricityGeneratorProbe\": ");
				WriteFocusedNestedObjectProbe(sb, value2, indent + 2);
				sb.AppendLine(",");
				object value3 = GetPropertyValue(entity, "MechPowerGenerator") ?? GetPropertyValue(entity, "MechanicalPowerGenerator") ?? GetPrivateFieldValue(entity, "m_mechPowerGenerator") ?? GetPrivateFieldValue(entity, "m_mechanicalPowerGenerator") ?? GetPrivateFieldValue(entity, "m_powerGenerator");
				sb.AppendLine(text + "  \"mechanicalGeneratorProbe\": ");
				WriteFocusedNestedObjectProbe(sb, value3, indent + 2);
				sb.AppendLine(",");
				object value4 = GetPropertyValue(entity, "InputBuffer") ?? GetPrivateFieldValue(entity, "m_inputBuffer") ?? GetPrivateFieldValue(entity, "m_inputBuffers");
				sb.AppendLine(text + "  \"inputBufferProbe\": ");
				WriteFocusedNestedObjectProbe(sb, value4, indent + 2);
				sb.AppendLine(",");
				object value5 = GetPropertyValue(entity, "OutputBuffer") ?? GetPrivateFieldValue(entity, "m_outputBuffer") ?? GetPrivateFieldValue(entity, "m_outputBuffers");
				sb.AppendLine(text + "  \"outputBufferProbe\": ");
				WriteFocusedNestedObjectProbe(sb, value5, indent + 2);
				sb.AppendLine();
				sb.Append(text + "}");
			}
			catch (Exception ex)
			{
				sb.AppendLine(text + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
				sb.Append(text + "}");
			}
		}

		private static void WriteFocusedPropertyProbe(StringBuilder sb, object target, string propertyName, int indent)
		{
			string text = new string(' ', indent);
			try
			{
				if (target == null)
				{
					sb.Append(text + "\"" + EscapeJson(propertyName) + "\": { \"found\": false, \"isNullTarget\": true }");
					return;
				}
				PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (property == null || property.GetIndexParameters().Length != 0)
				{
					sb.Append(text + "\"" + EscapeJson(propertyName) + "\": { \"found\": false }");
					return;
				}
				object value = property.GetValue(target, null);
				sb.Append(text + "\"" + EscapeJson(propertyName) + "\": { ");
				sb.Append("\"found\": true, ");
				sb.Append("\"declaredType\": \"" + EscapeJson(property.PropertyType.FullName) + "\", ");
				sb.Append("\"isNull\": " + ((value == null) ? "true" : "false") + ", ");
				sb.Append("\"runtimeType\": \"" + EscapeJson((value == null) ? "" : value.GetType().FullName) + "\", ");
				sb.Append("\"value\": \"" + EscapeJson((value == null) ? "" : value.ToString()) + "\"");
				sb.Append(" }");
			}
			catch (Exception ex)
			{
				sb.Append(text + "\"" + EscapeJson(propertyName) + "\": { ");
				sb.Append("\"found\": false, ");
				sb.Append("\"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
				sb.Append(" }");
			}
		}

		private static void WriteFocusedFieldProbe(StringBuilder sb, object target, string fieldName, int indent)
		{
			string text = new string(' ', indent);
			try
			{
				if (target == null)
				{
					sb.Append(text + "\"" + EscapeJson(fieldName) + "\": { \"found\": false, \"isNullTarget\": true }");
					return;
				}
				FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (field == null)
				{
					sb.Append(text + "\"" + EscapeJson(fieldName) + "\": { \"found\": false }");
					return;
				}
				object value = field.GetValue(target);
				sb.Append(text + "\"" + EscapeJson(fieldName) + "\": { ");
				sb.Append("\"found\": true, ");
				sb.Append("\"declaredType\": \"" + EscapeJson(field.FieldType.FullName) + "\", ");
				sb.Append("\"isNull\": " + ((value == null) ? "true" : "false") + ", ");
				sb.Append("\"runtimeType\": \"" + EscapeJson((value == null) ? "" : value.GetType().FullName) + "\", ");
				sb.Append("\"value\": \"" + EscapeJson((value == null) ? "" : value.ToString()) + "\"");
				sb.Append(" }");
			}
			catch (Exception ex)
			{
				sb.Append(text + "\"" + EscapeJson(fieldName) + "\": { ");
				sb.Append("\"found\": false, ");
				sb.Append("\"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
				sb.Append(" }");
			}
		}

		private static void WriteFocusedNestedObjectProbe(StringBuilder sb, object value, int indent)
		{
			string text = new string(' ', indent);
			if (value == null)
			{
				sb.AppendLine(text + "null");
				return;
			}
			try
			{
				Type type = value.GetType();
				sb.AppendLine(text + "{");
				sb.AppendLine(text + "  \"runtimeType\": \"" + EscapeJson(type.FullName) + "\",");
				sb.AppendLine(text + "  \"toString\": \"" + EscapeJson(value.ToString()) + "\",");
				WriteFocusedMemberNameScan(sb, type, "interestingMembers", indent + 2);
				sb.AppendLine(",");
				string[] array = new string[22]
				{
					"Id", "Product", "Quantity", "Capacity", "UsableCapacity", "IsFull", "IsEmpty", "InputProduct", "OutputProduct", "InputProducts",
					"OutputProducts", "Inputs", "Outputs", "Duration", "OutputElectricity", "MaxGenerationCapacity", "GeneratedElectricityThisTick", "GeneratedMechPowerThisTick", "ProducedMechPowerThisTick", "UsedMechPowerThisTick",
					"CurrentState", "State"
				};
				sb.AppendLine(text + "  \"candidateProperties\": {");
				for (int i = 0; i < array.Length; i++)
				{
					WriteFocusedPropertyProbe(sb, value, array[i], indent + 4);
					if (i < array.Length - 1)
					{
						sb.Append(",");
					}
					sb.AppendLine();
				}
				sb.AppendLine(text + "  }");
				sb.Append(text + "}");
			}
			catch (Exception ex)
			{
				sb.AppendLine(text + "{");
				sb.AppendLine(text + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
				sb.Append(text + "}");
			}
		}

		private static void WriteFocusedMemberNameScan(StringBuilder sb, Type type, string propertyName, int indent)
		{
			string text = new string(' ', indent);
			string[] interestingTerms = new string[18]
			{
				"power", "electric", "mech", "steam", "input", "output", "buffer", "product", "recipe", "capacity",
				"generation", "generated", "state", "proto", "duration", "fuel", "consumer", "producer"
			};
			var list = (from p in type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				where p.GetIndexParameters().Length == 0 && IsInteresting(p.Name)
				select new
				{
					kind = "property",
					name = p.Name,
					typeName = p.PropertyType.FullName
				} into p
				orderby p.name
				select p).Take(120).ToList();
			var list2 = (from f in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				where IsInteresting(f.Name)
				select new
				{
					kind = "field",
					name = f.Name,
					typeName = f.FieldType.FullName
				} into f
				orderby f.name
				select f).Take(120).ToList();
			sb.AppendLine(text + "\"" + EscapeJson(propertyName) + "\": [");
			int num = list.Count + list2.Count;
			int num2 = 0;
			foreach (var item in list)
			{
				sb.Append(text + "  { ");
				sb.Append("\"kind\": \"" + EscapeJson(item.kind) + "\", ");
				sb.Append("\"name\": \"" + EscapeJson(item.name) + "\", ");
				sb.Append("\"type\": \"" + EscapeJson(item.typeName) + "\"");
				sb.Append(" }");
				num2++;
				if (num2 < num)
				{
					sb.Append(",");
				}
				sb.AppendLine();
			}
			foreach (var item2 in list2)
			{
				sb.Append(text + "  { ");
				sb.Append("\"kind\": \"" + EscapeJson(item2.kind) + "\", ");
				sb.Append("\"name\": \"" + EscapeJson(item2.name) + "\", ");
				sb.Append("\"type\": \"" + EscapeJson(item2.typeName) + "\"");
				sb.Append(" }");
				num2++;
				if (num2 < num)
				{
					sb.Append(",");
				}
				sb.AppendLine();
			}
			sb.Append(text + "]");
			bool IsInteresting(string name)
			{
				string lower = name.ToLowerInvariant();
				return interestingTerms.Any((string term) => lower.Contains(term));
			}
		}

		private static string BuildEntityNameProbeJson(IEntitiesManager entitiesManager)
		{
			string[] array = new string[4] { "Mafi.Base.Prototypes.Machines.PowerGenerators.ElectricityGeneratorFromProduct", "Mafi.Base.Prototypes.Machines.PowerGenerators.ElectricityGeneratorFromMechPower", "Mafi.Base.Prototypes.Machines.PowerGenerators.MechPowerGeneratorFromProduct", "Mafi.Base.Prototypes.Machines.SolarElectricityGenerator" };
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("{");
			stringBuilder.AppendLine("  \"exportType\": \"entityNameProbe\",");
			stringBuilder.AppendLine("  \"purpose\": \"Focused probe to discover player-renamed/custom machine names versus default prototype titles.\",");
			stringBuilder.AppendLine("  \"searchHint\": \"Rename machines in-game to obvious names like CALDER TEST TURBINE, then search this file for CALDER.\",");
			stringBuilder.AppendLine("  \"types\": [");
			for (int i = 0; i < array.Length; i++)
			{
				WriteEntityNameTypeProbe(stringBuilder, entitiesManager, array[i], 4);
				if (i < array.Length - 1)
				{
					stringBuilder.Append(",");
				}
				stringBuilder.AppendLine();
			}
			stringBuilder.AppendLine("  ]");
			stringBuilder.AppendLine("}");
			return stringBuilder.ToString();
		}

		private static void WriteEntityNameTypeProbe(StringBuilder sb, IEntitiesManager entitiesManager, string typeName, int indent)
		{
			string text = new string(' ', indent);
			sb.AppendLine(text + "{");
			sb.AppendLine(text + "  \"typeName\": \"" + EscapeJson(typeName) + "\",");
			try
			{
				Type type = FindTypeByFullName(typeName);
				if (type == null)
				{
					sb.AppendLine(text + "  \"typeFound\": false");
					sb.Append(text + "}");
					return;
				}
				List<object> allEntitiesOfTypeReflective = GetAllEntitiesOfTypeReflective(entitiesManager, type);
				sb.AppendLine(text + "  \"typeFound\": true,");
				sb.AppendLine(text + "  \"assembly\": \"" + EscapeJson(type.Assembly.GetName().Name) + "\",");
				sb.AppendLine(text + "  \"entityCount\": " + allEntitiesOfTypeReflective.Count + ",");
				WriteNameMemberSummary(sb, type, "nameLikeMembersOnType", indent + 2);
				sb.AppendLine(",");
				sb.AppendLine(text + "  \"samples\": [");
				int num = Math.Min(allEntitiesOfTypeReflective.Count, 100);
				for (int i = 0; i < num; i++)
				{
					WriteEntityNameObjectProbe(sb, allEntitiesOfTypeReflective[i], indent + 4);
					if (i < num - 1)
					{
						sb.Append(",");
					}
					sb.AppendLine();
				}
				sb.AppendLine(text + "  ]");
				sb.Append(text + "}");
			}
			catch (Exception ex)
			{
				sb.AppendLine(text + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
				sb.Append(text + "}");
			}
		}

		private static void WriteEntityNameObjectProbe(StringBuilder sb, object entity, int indent)
		{
			string text = new string(' ', indent);
			sb.AppendLine(text + "{");
			if (entity == null)
			{
				sb.AppendLine(text + "  \"isNull\": true");
				sb.Append(text + "}");
				return;
			}
			try
			{
				Type type = entity.GetType();
				sb.AppendLine(text + "  \"runtimeType\": \"" + EscapeJson(type.FullName) + "\",");
				sb.AppendLine(text + "  \"toString\": \"" + EscapeJson(entity.ToString()) + "\",");
				WriteNameCandidateValue(sb, entity, "Id", indent + 2);
				sb.AppendLine(",");
				WriteNameCandidateValue(sb, entity, "DefaultTitle", indent + 2);
				sb.AppendLine(",");
				WriteNameCandidateValue(sb, entity, "Title", indent + 2);
				sb.AppendLine(",");
				WriteNameCandidateValue(sb, entity, "Name", indent + 2);
				sb.AppendLine(",");
				WriteNameCandidateValue(sb, entity, "DisplayName", indent + 2);
				sb.AppendLine(",");
				WriteNameCandidateValue(sb, entity, "DisplayTitle", indent + 2);
				sb.AppendLine(",");
				WriteNameCandidateValue(sb, entity, "CustomName", indent + 2);
				sb.AppendLine(",");
				WriteNameCandidateValue(sb, entity, "CustomTitle", indent + 2);
				sb.AppendLine(",");
				WriteNameCandidateValue(sb, entity, "UserName", indent + 2);
				sb.AppendLine(",");
				WriteNameCandidateValue(sb, entity, "UserTitle", indent + 2);
				sb.AppendLine(",");
				WriteNameCandidateValue(sb, entity, "Label", indent + 2);
				sb.AppendLine(",");
				WriteNameCandidateValue(sb, entity, "Caption", indent + 2);
				sb.AppendLine(",");
				sb.AppendLine(text + "  \"nameLikeMemberValues\": [");
				WriteNameLikeMemberValues(sb, entity, indent + 4);
				sb.AppendLine();
				sb.AppendLine(text + "  ],");
				object value = GetPrivateFieldValue(entity, "m_proto") ?? GetPropertyValue(entity, "Prototype") ?? GetPropertyValue(entity, "Proto");
				sb.AppendLine(text + "  \"prototype\": ");
				WriteNameNestedProbe(sb, value, indent + 2);
				sb.AppendLine();
				sb.Append(text + "}");
			}
			catch (Exception ex)
			{
				sb.AppendLine(text + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
				sb.Append(text + "}");
			}
		}

		private static void WriteNameCandidateValue(StringBuilder sb, object target, string memberName, int indent)
		{
			string text = new string(' ', indent);
			sb.Append(text + "\"" + EscapeJson(memberName) + "\": ");
			try
			{
				object memberValueByExactName = GetMemberValueByExactName(target, memberName);
				if (memberValueByExactName == null)
				{
					sb.Append("{ \"found\": false }");
					return;
				}
				sb.Append("{ ");
				sb.Append("\"found\": true, ");
				sb.Append("\"runtimeType\": \"" + EscapeJson(memberValueByExactName.GetType().FullName) + "\", ");
				sb.Append("\"value\": \"" + EscapeJson(memberValueByExactName.ToString()) + "\"");
				sb.Append(" }");
			}
			catch (Exception ex)
			{
				sb.Append("{ ");
				sb.Append("\"found\": false, ");
				sb.Append("\"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
				sb.Append(" }");
			}
		}

		private static object GetMemberValueByExactName(object target, string memberName)
		{
			if (target == null)
			{
				return null;
			}
			Type type = target.GetType();
			PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			PropertyInfo[] array = properties;
			foreach (PropertyInfo propertyInfo in array)
			{
				if (!(propertyInfo.Name != memberName) && propertyInfo.GetIndexParameters().Length == 0)
				{
					try
					{
						return propertyInfo.GetValue(target, null);
					}
					catch
					{
					}
				}
			}
			FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			FieldInfo[] array2 = fields;
			foreach (FieldInfo fieldInfo in array2)
			{
				if (!(fieldInfo.Name != memberName))
				{
					try
					{
						return fieldInfo.GetValue(target);
					}
					catch
					{
					}
				}
			}
			return null;
		}

		private static void WriteNameLikeMemberValues(StringBuilder sb, object target, int indent)
		{
			string text = new string(' ', indent);
			if (target == null)
			{
				return;
			}
			Type type = target.GetType();
			string[] terms = new string[7] { "name", "title", "display", "label", "caption", "custom", "user" };
			List<string> list = new List<string>();
			PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (PropertyInfo propertyInfo in properties)
			{
				if (propertyInfo.GetIndexParameters().Length == 0 && IsInteresting(propertyInfo.Name))
				{
					try
					{
						object value = propertyInfo.GetValue(target, null);
						list.Add("{ \"kind\": \"property\", \"name\": \"" + EscapeJson(propertyInfo.Name) + "\", \"declaredType\": \"" + EscapeJson(propertyInfo.PropertyType.FullName) + "\", \"valueType\": \"" + EscapeJson((value == null) ? "" : value.GetType().FullName) + "\", \"value\": \"" + EscapeJson((value == null) ? "" : value.ToString()) + "\" }");
					}
					catch (Exception ex)
					{
						list.Add("{ \"kind\": \"property\", \"name\": \"" + EscapeJson(propertyInfo.Name) + "\", \"declaredType\": \"" + EscapeJson(propertyInfo.PropertyType.FullName) + "\", \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\" }");
					}
				}
			}
			FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (FieldInfo fieldInfo in fields)
			{
				if (IsInteresting(fieldInfo.Name))
				{
					try
					{
						object value2 = fieldInfo.GetValue(target);
						list.Add("{ \"kind\": \"field\", \"name\": \"" + EscapeJson(fieldInfo.Name) + "\", \"declaredType\": \"" + EscapeJson(fieldInfo.FieldType.FullName) + "\", \"valueType\": \"" + EscapeJson((value2 == null) ? "" : value2.GetType().FullName) + "\", \"value\": \"" + EscapeJson((value2 == null) ? "" : value2.ToString()) + "\" }");
					}
					catch (Exception ex2)
					{
						list.Add("{ \"kind\": \"field\", \"name\": \"" + EscapeJson(fieldInfo.Name) + "\", \"declaredType\": \"" + EscapeJson(fieldInfo.FieldType.FullName) + "\", \"error\": \"" + EscapeJson(ex2.GetType().Name + ": " + ex2.Message) + "\" }");
					}
				}
			}
			for (int k = 0; k < list.Count; k++)
			{
				sb.Append(text + list[k]);
				if (k < list.Count - 1)
				{
					sb.Append(",");
				}
				sb.AppendLine();
			}
			bool IsInteresting(string name)
			{
				string lower = name.ToLowerInvariant();
				return terms.Any((string term) => lower.Contains(term));
			}
		}

		private static void WriteNameMemberSummary(StringBuilder sb, Type type, string propertyName, int indent)
		{
			string text = new string(' ', indent);
			string[] terms = new string[7] { "name", "title", "display", "label", "caption", "custom", "user" };
			List<string> list = new List<string>();
			PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (PropertyInfo propertyInfo in properties)
			{
				if (propertyInfo.GetIndexParameters().Length == 0 && IsInteresting(propertyInfo.Name))
				{
					list.Add("{ \"kind\": \"property\", \"name\": \"" + EscapeJson(propertyInfo.Name) + "\", \"type\": \"" + EscapeJson(propertyInfo.PropertyType.FullName) + "\" }");
				}
			}
			FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (FieldInfo fieldInfo in fields)
			{
				if (IsInteresting(fieldInfo.Name))
				{
					list.Add("{ \"kind\": \"field\", \"name\": \"" + EscapeJson(fieldInfo.Name) + "\", \"type\": \"" + EscapeJson(fieldInfo.FieldType.FullName) + "\" }");
				}
			}
			sb.AppendLine(text + "\"" + EscapeJson(propertyName) + "\": [");
			for (int k = 0; k < list.Count; k++)
			{
				sb.Append(text + "  " + list[k]);
				if (k < list.Count - 1)
				{
					sb.Append(",");
				}
				sb.AppendLine();
			}
			sb.Append(text + "]");
			bool IsInteresting(string name)
			{
				string lower = name.ToLowerInvariant();
				return terms.Any((string term) => lower.Contains(term));
			}
		}

		private static void WriteNameNestedProbe(StringBuilder sb, object value, int indent)
		{
			string text = new string(' ', indent);
			if (value == null)
			{
				sb.AppendLine(text + "null");
				return;
			}
			sb.AppendLine(text + "{");
			sb.AppendLine(text + "  \"runtimeType\": \"" + EscapeJson(value.GetType().FullName) + "\",");
			sb.AppendLine(text + "  \"toString\": \"" + EscapeJson(value.ToString()) + "\",");
			WriteNameMemberSummary(sb, value.GetType(), "nameLikeMembers", indent + 2);
			sb.AppendLine(",");
			sb.AppendLine(text + "  \"nameLikeMemberValues\": [");
			WriteNameLikeMemberValues(sb, value, indent + 4);
			sb.AppendLine();
			sb.AppendLine(text + "  ]");
			sb.Append(text + "}");
		}

		private static string CleanOptionString(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return "";
			}
			string text = value.Trim();
			if (text.StartsWith("Some: "))
			{
				return text.Substring("Some: ".Length).Trim();
			}
			if (text.StartsWith("None:"))
			{
				return "";
			}
			return text;
		}
	}
}
