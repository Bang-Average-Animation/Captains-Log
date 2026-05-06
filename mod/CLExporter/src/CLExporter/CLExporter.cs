using Mafi;
using Mafi.Collections;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core.Factory.Machines;
using Mafi.Core.Factory.Recipes;
using Mafi.Core.Mods;
using Mafi.Core.Products;
using Mafi.Core.Prototypes;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Mafi.Core.Entities;
using Mafi.Core.Factory.Machines;

namespace CLExporter;

public sealed class CLExporter : DataOnlyMod, IMod
{

	private IEntitiesManager m_entitiesManager;
	private int m_machineExportCount = 0;

	private static int s_nextInstanceId = 0;
	private static int s_activeInstanceId = 0;

	private int m_instanceId = 0;

	private object m_electricityManager;

	private DependencyResolver m_resolver;
	private readonly object m_exportLock = new object();
	private System.Timers.Timer m_refreshTimer;

	private const int MAX_MACHINE_EXPORTS = 200;
	private const double REFRESH_INTERVAL_MS = 10000;




	public CLExporter(ModManifest manifest) : base(manifest)
	{
		Log.Info("CLExporter: constructed");

		string outputPath = Path.Combine(
			manifest.RootDirectoryPath,
			"coi_mod_lifecycle_signature_probe.json"
		);

		string json = BuildMemberProbeJson();

		File.WriteAllText(outputPath, json);

		Log.Info("CLExporter: wrote mod lifecycle signature probe export to " + outputPath);
	}

	void IMod.Initialize(DependencyResolver resolver, bool gameWasLoaded)
	{
		Log.Info("CLExporter: explicit IMod.Initialize called - subscribing to entity events and timer");

		m_instanceId = System.Threading.Interlocked.Increment(ref s_nextInstanceId);
		System.Threading.Volatile.Write(ref s_activeInstanceId, m_instanceId);

		Log.Info("CLExporter: active exporter instance set to " + m_instanceId);

		try
		{
			m_resolver = resolver;
			m_entitiesManager = resolver.Resolve<IEntitiesManager>();

			m_electricityManager = ResolveTypeReflective(
		resolver,
		FindTypeByFullName("Mafi.Core.Factory.ElectricPower.IElectricityManager")
	);

			m_entitiesManager.EntityAddedFull.AddNonSaveable(
				this,
				OnEntityAddedFull
			);

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
		Log.Info("CLExporter: exporting products and recipes catalogue");

		string productsPath = Path.Combine(
			this.Manifest.RootDirectoryPath,
			"coi_products.json"
		);

		string productsJson = BuildProductsJson(registrator);

		File.WriteAllText(productsPath, productsJson);

		Log.Info("CLExporter: wrote products export to " + productsPath);

		string recipesPath = Path.Combine(
			this.Manifest.RootDirectoryPath,
			"coi_recipes.json"
		);

		string recipesJson = BuildRecipeSampleJson(registrator);

		File.WriteAllText(recipesPath, recipesJson);

		Log.Info("CLExporter: wrote recipes export to " + recipesPath);
	}

	public override void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues)
	{

	}

	private static string BuildMemberProbeJson()
	{
		string[] targetTypeNames = new[] {
			"Mafi.Core.Mods.DataOnlyMod",
			"Mafi.Core.Mods.IMod",
			"Mafi.Core.Mods.IModConfig",
			"Mafi.Core.Mods.ModJsonConfig",
			"Mafi.Core.Mods.ModManifest",
			"Mafi.Core.Mods.ProtoRegistrator",
			"Mafi.Core.Mods.ModDependencyResolver",
			"Mafi.Core.Mods.ModDependencies",
			"Mafi.Core.Mods.IModDependency",
			"Mafi.Core.Entities.IEntitiesManager",
			"Mafi.Core.Entities.EntitiesManager"
		};

		var sb = new StringBuilder();

		sb.AppendLine("{");
		sb.AppendLine("  \"exportType\": \"memberProbe\",");
		sb.AppendLine("  \"types\": [");

		for (int i = 0; i < targetTypeNames.Length; i++)
		{
			string typeName = targetTypeNames[i];
			Type type = FindType(typeName);

			sb.AppendLine("    {");
			sb.AppendLine("      \"name\": \"" + EscapeJson(typeName) + "\",");

			if (type == null)
			{
				sb.AppendLine("      \"found\": false");
			}
			else
			{
				sb.AppendLine("      \"found\": true,");
				sb.AppendLine("      \"assembly\": \"" + EscapeJson(type.Assembly.GetName().Name) + "\",");

				var properties = type
					.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
					.Select(p => p.PropertyType.Name + " " + p.Name)
					.OrderBy(x => x)
					.Take(120)
					.ToList();

				var fields = type
					.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
					.Select(f => f.FieldType.Name + " " + f.Name)
					.OrderBy(x => x)
					.Take(120)
					.ToList();

				var methods = type
					.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
					.Select(m =>
						m.ReturnType.Name + " " +
						m.Name + "(" +
						string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name)) +
						")"
					)
					.Distinct()
					.OrderBy(x => x)
					.Take(160)
					.ToList();

				WriteStringArray(sb, "properties", properties, 6);
				sb.AppendLine(",");
				WriteStringArray(sb, "fields", fields, 6);
				sb.AppendLine(",");
				WriteStringArray(sb, "methods", methods, 6);
				sb.AppendLine();
			}

			sb.Append("    }");

			if (i < targetTypeNames.Length - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.AppendLine("  ]");
		sb.AppendLine("}");

		return sb.ToString();
	}

	private static string BuildProductsJson(ProtoRegistrator registrator)
	{
		ProtosDb db = registrator.PrototypesDb;

		var products = db.All<ProductProto>()
			.OrderBy(p => p.Id.ToString())
			.ToList();

		var sb = new StringBuilder();

		sb.AppendLine("{");
		sb.AppendLine("  \"exportType\": \"products\",");
		sb.AppendLine("  \"productCount\": " + products.Count + ",");
		sb.AppendLine("  \"products\": [");

		for (int i = 0; i < products.Count; i++)
		{
			var product = products[i];

			sb.AppendLine("    {");
			sb.AppendLine("      \"productId\": \"" + EscapeJson(product.Id.ToString()) + "\",");
			sb.AppendLine("      \"name\": \"" + EscapeJson(product.Strings.Name.ToString()) + "\",");
			sb.AppendLine("      \"descShort\": \"" + EscapeJson(product.Strings.DescShort.ToString()) + "\",");
			sb.AppendLine("      \"type\": \"" + EscapeJson(product.Type.ToString()) + "\",");
			sb.AppendLine("      \"iconPath\": \"" + EscapeJson(product.IconPath ?? "") + "\"");
			sb.Append("    }");

			if (i < products.Count - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.AppendLine("  ]");
		sb.AppendLine("}");

		return sb.ToString();
	}

	private static string BuildRecipeSampleJson(ProtoRegistrator registrator)
	{
		ProtosDb db = registrator.PrototypesDb;

		var recipes = db.All<RecipeProto>()
			.OrderBy(r => r.Id.ToString())
			.ToList();

		var sb = new StringBuilder();

		sb.AppendLine("{");
		sb.AppendLine("  \"exportType\": \"recipes\",");
		sb.AppendLine("  \"recipeCount\": " + recipes.Count + ",");
		sb.AppendLine("  \"recipes\": [");

		for (int i = 0; i < recipes.Count; i++)
		{
			var recipe = recipes[i];

			sb.AppendLine("    {");
			sb.AppendLine("      \"id\": \"" + EscapeJson(recipe.Id.ToString()) + "\",");
			sb.AppendLine("      \"duration\": \"" + EscapeJson(recipe.Duration.ToString()) + "\",");
			sb.AppendLine("      \"inputs\": [");

			WriteRecipeInputs(sb, recipe.AllUserVisibleInputs, 8);

			sb.AppendLine("      ],");
			sb.AppendLine("      \"outputs\": [");

			WriteRecipeOutputs(sb, recipe.AllUserVisibleOutputs, 8);

			sb.AppendLine("      ]");
			sb.Append("    }");

			if (i < recipes.Count - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.AppendLine("  ]");
		sb.AppendLine("}");

		return sb.ToString();
	}

	private static void WriteRecipeInputs(StringBuilder sb, ImmutableArray<RecipeInput> values, int indent)
	{
		string pad = new string(' ', indent);

		for (int i = 0; i < values.Length; i++)
		{
			var value = values[i];

			sb.Append(pad);
			sb.Append("{ ");
			sb.Append("\"productId\": \"" + EscapeJson(value.Product.Id.ToString()) + "\", ");
			sb.Append("\"quantity\": \"" + EscapeJson(value.Quantity.ToString()) + "\"");
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
		string pad = new string(' ', indent);

		for (int i = 0; i < values.Length; i++)
		{
			var value = values[i];

			sb.Append(pad);
			sb.Append("{ ");
			sb.Append("\"productId\": \"" + EscapeJson(value.Product.Id.ToString()) + "\", ");
			sb.Append("\"quantity\": \"" + EscapeJson(value.Quantity.ToString()) + "\", ");
			sb.Append("\"triggerAtStart\": " + (value.TriggerAtStart ? "true" : "false"));
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
		ProtosDb db = registrator.PrototypesDb;

		var machines = db.All<MachineProto>()
			.OrderBy(m => m.Id.ToString())
			.ToList();

		var sb = new StringBuilder();

		sb.AppendLine("{");
		sb.AppendLine("  \"exportType\": \"machineRecipes\",");
		sb.AppendLine("  \"machineCount\": " + machines.Count + ",");
		sb.AppendLine("  \"machines\": [");

		for (int i = 0; i < machines.Count; i++)
		{
			var machine = machines[i];

			sb.AppendLine("    {");
			sb.AppendLine("      \"machineId\": \"" + EscapeJson(machine.Id.ToString()) + "\",");
			sb.AppendLine("      \"recipes\": [");

			for (int r = 0; r < machine.Recipes.Count; r++)
			{
				var recipe = machine.Recipes[r];

				sb.Append("        ");
				sb.Append("\"" + EscapeJson(recipe.Id.ToString()) + "\"");

				if (r < machine.Recipes.Count - 1)
				{
					sb.Append(",");
				}

				sb.AppendLine();
			}

			sb.AppendLine("      ]");
			sb.Append("    }");

			if (i < machines.Count - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.AppendLine("  ]");
		sb.AppendLine("}");

		return sb.ToString();
	}

	private static string BuildCatalogueCountsJson(ProtoRegistrator registrator)
	{
		ProtosDb db = registrator.PrototypesDb;

		var products = db.All<ProductProto>().ToList();
		var machines = db.All<MachineProto>().ToList();
		var recipes = db.All<RecipeProto>().ToList();

		var sb = new StringBuilder();

		sb.AppendLine("{");
		sb.AppendLine("  \"exportType\": \"catalogueCounts\",");
		sb.AppendLine("  \"products\": " + products.Count + ",");
		sb.AppendLine("  \"machines\": " + machines.Count + ",");
		sb.AppendLine("  \"recipes\": " + recipes.Count);
		sb.AppendLine("}");

		return sb.ToString();
	}

	private static string BuildTypeProbeJson()
	{
		string[] keywords = new[] {
			"Proto",
			"Recipe",
			"Product",
			"Machine",
			"Registrator",
			"Manager"
		};

		var matchingTypes =
			AppDomain.CurrentDomain
				.GetAssemblies()
				.Where(a =>
					a.GetName().Name.StartsWith("Mafi", StringComparison.OrdinalIgnoreCase)
					|| a.GetName().Name.StartsWith("Assembly", StringComparison.OrdinalIgnoreCase)
				)
				.SelectMany(a =>
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
				.Where(t => keywords.Any(k => t.FullName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
				.Select(t => new
				{
					Assembly = t.Assembly.GetName().Name,
					Name = t.FullName
				})
				.OrderBy(t => t.Assembly)
				.ThenBy(t => t.Name)
				.Take(2000)
				.ToList();

		var sb = new StringBuilder();

		sb.AppendLine("{");
		sb.AppendLine("  \"exportType\": \"typeProbe\",");
		sb.AppendLine("  \"matchCount\": " + matchingTypes.Count + ",");
		sb.AppendLine("  \"types\": [");

		for (int i = 0; i < matchingTypes.Count; i++)
		{
			var t = matchingTypes[i];

			sb.Append("    { ");
			sb.Append("\"assembly\": \"" + EscapeJson(t.Assembly) + "\", ");
			sb.Append("\"name\": \"" + EscapeJson(t.Name) + "\"");
			sb.Append(" }");

			if (i < matchingTypes.Count - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.AppendLine("  ]");
		sb.AppendLine("}");

		return sb.ToString();
	}

	private static string BuildRuntimeTypeProbeJson()
	{
		var matchingTypes =
			AppDomain.CurrentDomain
				.GetAssemblies()
				.Where(a =>
					a.GetName().Name.StartsWith("Mafi", StringComparison.OrdinalIgnoreCase)
				)
				.SelectMany(a =>
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
				.Where(t =>
					t.FullName.IndexOf("Machine", StringComparison.OrdinalIgnoreCase) >= 0
					|| t.FullName.IndexOf("Entity", StringComparison.OrdinalIgnoreCase) >= 0
					|| t.FullName.IndexOf("Manager", StringComparison.OrdinalIgnoreCase) >= 0
					|| t.FullName.IndexOf("World", StringComparison.OrdinalIgnoreCase) >= 0
				)
				.Select(t => new
				{
					Assembly = t.Assembly.GetName().Name,
					Name = t.FullName
				})
				.OrderBy(t => t.Assembly)
				.ThenBy(t => t.Name)
				.Take(3000)
				.ToList();

		var sb = new StringBuilder();

		sb.AppendLine("{");
		sb.AppendLine("  \"exportType\": \"runtimeTypeProbe\",");
		sb.AppendLine("  \"matchCount\": " + matchingTypes.Count + ",");
		sb.AppendLine("  \"types\": [");

		for (int i = 0; i < matchingTypes.Count; i++)
		{
			var t = matchingTypes[i];

			sb.Append("    { ");
			sb.Append("\"assembly\": \"" + EscapeJson(t.Assembly) + "\", ");
			sb.Append("\"name\": \"" + EscapeJson(t.Name) + "\"");
			sb.Append(" }");

			if (i < matchingTypes.Count - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.AppendLine("  ]");
		sb.AppendLine("}");

		return sb.ToString();
	}

	private static string BuildRuntimeObjectProbeJson(object target)
	{
		Type type = target.GetType();

		var properties = type
			.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
			.Select(p => p.PropertyType.FullName + " " + p.Name)
			.OrderBy(x => x)
			.Take(120)
			.ToList();

		var fields = type
			.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
			.Select(f => f.FieldType.FullName + " " + f.Name)
			.OrderBy(x => x)
			.Take(120)
			.ToList();

		var methods = type
			.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
			.Select(m =>
				m.ReturnType.FullName + " " +
				m.Name + "(" +
				string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name)) +
				")"
			)
			.Distinct()
			.OrderBy(x => x)
			.Take(160)
			.ToList();

		var sb = new StringBuilder();

		sb.AppendLine("{");
		sb.AppendLine("  \"exportType\": \"runtimeObjectProbe\",");
		sb.AppendLine("  \"runtimeType\": \"" + EscapeJson(type.FullName) + "\",");
		sb.AppendLine("  \"assembly\": \"" + EscapeJson(type.Assembly.GetName().Name) + "\",");

		WriteStringArray(sb, "properties", properties, 2);
		sb.AppendLine(",");

		WriteStringArray(sb, "fields", fields, 2);
		sb.AppendLine(",");

		WriteStringArray(sb, "methods", methods, 2);
		sb.AppendLine();

		sb.AppendLine("}");

		return sb.ToString();
	}
	private static string BuildPowerFarmTypeProbeJson()
	{
		string[] keywords = new[] {
		"Power",
		"Electric",
		"Electricity",
		"Generator",
		"Grid",
		"Energy",
		"Turbine",
		"Boiler",
		"Solar",
		"Nuclear",
		"Reactor",
		"Farm",
		"Crop",
		"Field",
		"Harvest",
		"Irrigation",
		"Fertility",
		"Greenhouse",
		"Food"
	};

		var matchingTypes =
			AppDomain.CurrentDomain
				.GetAssemblies()
				.Where(a =>
					a.GetName().Name.StartsWith("Mafi", StringComparison.OrdinalIgnoreCase)
				)
				.SelectMany(a =>
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
				.Where(t =>
					t.FullName != null &&
					keywords.Any(k =>
						t.FullName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0
					)
				)
				.Select(t => new
				{
					Assembly = t.Assembly.GetName().Name,
					Name = t.FullName,
					BaseType = t.BaseType == null ? "" : t.BaseType.FullName,
					IsInterface = t.IsInterface,
					IsAbstract = t.IsAbstract
				})
				.OrderBy(t => t.Assembly)
				.ThenBy(t => t.Name)
				.Take(5000)
				.ToList();

		var sb = new StringBuilder();

		sb.AppendLine("{");
		sb.AppendLine("  \"exportType\": \"powerFarmTypeProbe\",");
		sb.AppendLine("  \"matchCount\": " + matchingTypes.Count + ",");
		sb.AppendLine("  \"types\": [");

		for (int i = 0; i < matchingTypes.Count; i++)
		{
			var t = matchingTypes[i];

			sb.Append("    { ");
			sb.Append("\"assembly\": \"" + EscapeJson(t.Assembly) + "\", ");
			sb.Append("\"name\": \"" + EscapeJson(t.Name) + "\", ");
			sb.Append("\"baseType\": \"" + EscapeJson(t.BaseType) + "\", ");
			sb.Append("\"isInterface\": " + (t.IsInterface ? "true" : "false") + ", ");
			sb.Append("\"isAbstract\": " + (t.IsAbstract ? "true" : "false"));
			sb.Append(" }");

			if (i < matchingTypes.Count - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.AppendLine("  ]");
		sb.AppendLine("}");

		return sb.ToString();
	}
	private static Type FindType(string fullName)
	{
		return AppDomain.CurrentDomain
			.GetAssemblies()
			.Select(a =>
			{
				try
				{
					return a.GetType(fullName, false);
				}
				catch
				{
					return null;
				}
			})
			.FirstOrDefault(t => t != null);
	}

	private static void WriteStringArray(StringBuilder sb, string name, System.Collections.Generic.List<string> values, int indent)
	{
		string pad = new string(' ', indent);

		sb.AppendLine(pad + "\"" + name + "\": [");

		for (int i = 0; i < values.Count; i++)
		{
			sb.Append(pad + "  \"" + EscapeJson(values[i]) + "\"");

			if (i < values.Count - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.Append(pad + "]");
	}

	private static string EscapeJson(string value)
	{
		if (value == null)
		{
			return "";
		}

		return value
			.Replace("\\", "\\\\")
			.Replace("\"", "\\\"");
	}

	private static string BuildWorldMachinesJson(
	System.Collections.Generic.List<Machine> machines,
	bool gameWasLoaded,
	string trigger,
	int exportCount
)
	{
		var sb = new StringBuilder();

		sb.AppendLine("{");
		sb.AppendLine("  \"exportType\": \"worldMachines\",");
		sb.AppendLine("  \"success\": true,");
		sb.AppendLine("  \"gameWasLoaded\": " + (gameWasLoaded ? "true" : "false") + ",");
		sb.AppendLine("  \"trigger\": \"" + EscapeJson(trigger) + "\",");
		sb.AppendLine("  \"exportCount\": " + exportCount + ",");
		sb.AppendLine("  \"machineCount\": " + machines.Count + ",");
		sb.AppendLine("  \"machines\": [");

		for (int i = 0; i < machines.Count; i++)
		{
			var machine = machines[i];
			var assignedRecipes = GetAssignedRecipeIds(machine);

			string title = machine.DefaultTitle.ToString();
			string customTitleRaw = GetPropertyString(machine, "CustomTitle");
			string customTitle = CleanOptionString(customTitleRaw);
			string displayTitle = string.IsNullOrWhiteSpace(customTitle) ? title : customTitle;

			sb.AppendLine("    {");
			sb.AppendLine("      \"entityId\": \"" + EscapeJson(machine.Id.ToString()) + "\",");
			sb.AppendLine("      \"machineId\": \"" + EscapeJson(machine.Prototype.Id.ToString()) + "\",");
			sb.AppendLine("      \"title\": \"" + EscapeJson(title) + "\",");
			sb.AppendLine("      \"customTitleRaw\": \"" + EscapeJson(customTitleRaw) + "\",");
			sb.AppendLine("      \"customTitle\": \"" + EscapeJson(customTitle) + "\",");
			sb.AppendLine("      \"displayTitle\": \"" + EscapeJson(displayTitle) + "\",");
			sb.AppendLine("      \"isConstructed\": " + (machine.IsConstructed ? "true" : "false") + ",");
			sb.AppendLine("      \"isEnabled\": " + (machine.IsEnabled ? "true" : "false") + ",");
			sb.AppendLine("      \"isPaused\": " + (machine.IsPaused ? "true" : "false") + ",");
			sb.AppendLine("      \"workedThisTick\": " + (machine.WorkedThisTick ? "true" : "false") + ",");
			sb.AppendLine("      \"currentState\": \"" + EscapeJson(machine.CurrentState.ToString()) + "\",");
			sb.AppendLine("      \"utilization\": \"" + EscapeJson(machine.Utilization.ToString()) + "\",");
			sb.AppendLine("      \"lastRecipeInProgressRaw\": \"" + EscapeJson(machine.LastRecipeInProgress.ToString()) + "\",");
			sb.AppendLine("      \"position\": \"" + EscapeJson(machine.Position3f.ToString()) + "\",");

			WriteMachineBufferArray(sb, "inputBuffers", machine, "m_inputBuffers", 6);
			sb.AppendLine(",");

			WriteMachineBufferArray(sb, "outputBuffers", machine, "m_outputBuffers", 6);
			sb.AppendLine(",");

			sb.AppendLine("      \"assignedRecipes\": [");
			for (int r = 0; r < assignedRecipes.Count; r++)
			{
				sb.Append("        \"" + EscapeJson(assignedRecipes[r]) + "\"");

				if (r < assignedRecipes.Count - 1)
				{
					sb.Append(",");
				}

				sb.AppendLine();
			}

			sb.AppendLine("      ]");
			sb.Append("    }");

			if (i < machines.Count - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.AppendLine("  ]");
		sb.AppendLine("}");

		return sb.ToString();
	}

	private static string BuildEntityEventsProbeJson(IEntitiesManager entitiesManager, bool gameWasLoaded)
	{
		var sb = new StringBuilder();

		sb.AppendLine("{");
		sb.AppendLine("  \"exportType\": \"entityEventProbe\",");
		sb.AppendLine("  \"success\": true,");
		sb.AppendLine("  \"gameWasLoaded\": " + (gameWasLoaded ? "true" : "false") + ",");
		sb.AppendLine("  \"events\": [");

		WriteRuntimeObjectSummary(sb, "EntityAdded", entitiesManager.EntityAdded, 4);
		sb.AppendLine(",");

		WriteRuntimeObjectSummary(sb, "StaticEntityAdded", entitiesManager.StaticEntityAdded, 4);
		sb.AppendLine(",");

		WriteRuntimeObjectSummary(sb, "EntityAddedFull", entitiesManager.EntityAddedFull, 4);
		sb.AppendLine();

		sb.AppendLine("  ]");
		sb.AppendLine("}");

		return sb.ToString();
	}

	private static void WriteRuntimeObjectSummary(StringBuilder sb, string label, object target, int indent)
	{
		string pad = new string(' ', indent);

		if (target == null)
		{
			sb.AppendLine(pad + "{");
			sb.AppendLine(pad + "  \"label\": \"" + EscapeJson(label) + "\",");
			sb.AppendLine(pad + "  \"isNull\": true");
			sb.Append(pad + "}");
			return;
		}

		Type type = target.GetType();

		var methods = type
			.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
			.Select(m =>
				m.ReturnType.Name + " " +
				m.Name + "(" +
				string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name)) +
				")"
			)
			.Distinct()
			.OrderBy(x => x)
			.Take(160)
			.ToList();

		var properties = type
			.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
			.Select(p => p.PropertyType.FullName + " " + p.Name)
			.OrderBy(x => x)
			.Take(80)
			.ToList();

		var fields = type
			.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
			.Select(f => f.FieldType.FullName + " " + f.Name)
			.OrderBy(x => x)
			.Take(80)
			.ToList();

		sb.AppendLine(pad + "{");
		sb.AppendLine(pad + "  \"label\": \"" + EscapeJson(label) + "\",");
		sb.AppendLine(pad + "  \"isNull\": false,");
		sb.AppendLine(pad + "  \"runtimeType\": \"" + EscapeJson(type.FullName) + "\",");
		sb.AppendLine(pad + "  \"assembly\": \"" + EscapeJson(type.Assembly.GetName().Name) + "\",");

		WriteStringArray(sb, "properties", properties, indent + 2);
		sb.AppendLine(",");

		WriteStringArray(sb, "fields", fields, indent + 2);
		sb.AppendLine(",");

		WriteStringArray(sb, "methods", methods, indent + 2);
		sb.AppendLine();

		sb.Append(pad + "}");
	}

	private bool IsActiveExporterInstance()
	{
		return m_instanceId != 0 &&
			m_instanceId == System.Threading.Volatile.Read(ref s_activeInstanceId);
	}

	private void OnEntityAddedFull(IEntity entity, EntityAddReason reason)
	{
		if (!IsActiveExporterInstance())
		{
			return;
		}

		if (m_machineExportCount >= MAX_MACHINE_EXPORTS)
		{
			return;
		}

		if (entity is Machine)
		{
			ExportWorldMachines(true, "entityAddedFull:" + reason.ToString());
		}
	}

	private void ExportWorldMachines(bool gameWasLoaded, string trigger)
	{
		if (!IsActiveExporterInstance())
		{
			Log.Info(
				"CLExporter: skipping export from inactive instance " +
				m_instanceId +
				". Active instance is " +
				System.Threading.Volatile.Read(ref s_activeInstanceId) +
				". Trigger=" +
				trigger
			);

			return;
		}

		lock (m_exportLock)
		{
			if (!IsActiveExporterInstance())
			{
				Log.Info(
					"CLExporter: skipping locked export from inactive instance " +
					m_instanceId +
					". Active instance is " +
					System.Threading.Volatile.Read(ref s_activeInstanceId) +
					". Trigger=" +
					trigger
				);

				return;
			}

			if (m_entitiesManager == null)
			{
				return;
			}

			var machines = m_entitiesManager
				.GetAllEntitiesOfType<Machine>()
				.OrderBy(m => m.Prototype.Id.ToString())
				.ThenBy(m => m.Id.ToString())
				.ToList();

			m_machineExportCount++;

			string outputPath = Path.Combine(
				this.Manifest.RootDirectoryPath,
				"coi_world_machines.json"
			);

			string json = BuildWorldMachinesJson(
				machines,
				gameWasLoaded,
				trigger,
				m_machineExportCount
			);

			File.WriteAllText(outputPath, json);

			Log.Info("CLExporter: wrote world machines export. Trigger=" + trigger + ", Count=" + machines.Count);
			string powerPath = Path.Combine(
		this.Manifest.RootDirectoryPath,
		"coi_power.json"
	);

			string powerJson = BuildPowerJson(m_electricityManager);

			File.WriteAllText(powerPath, powerJson);

			Log.Info("CLExporter: wrote power export to " + powerPath);

			string powerEntitiesPath = Path.Combine(
				this.Manifest.RootDirectoryPath,
				"coi_power_entities.json"
			);

			string powerAssetProbePath = Path.Combine(
				this.Manifest.RootDirectoryPath,
				"coi_power_asset_probe.json"
			);

			string powerAssetProbeJson = BuildPowerAssetProbeJson(m_entitiesManager);

			File.WriteAllText(powerAssetProbePath, powerAssetProbeJson);

			Log.Info("CLExporter: wrote focused power asset probe to " + powerAssetProbePath);

			string powerEntitiesJson = BuildPowerEntitiesJson(m_entitiesManager);

			File.WriteAllText(powerEntitiesPath, powerEntitiesJson);

			string entityNameProbePath = Path.Combine(
	this.Manifest.RootDirectoryPath,
	"coi_entity_name_probe.json"
);

			string entityNameProbeJson = BuildEntityNameProbeJson(m_entitiesManager);

			File.WriteAllText(entityNameProbePath, entityNameProbeJson);

			Log.Info("CLExporter: wrote entity name probe to " + entityNameProbePath);

			Log.Info("CLExporter: wrote power entities export to " + powerEntitiesPath);

			if (trigger == "timer" || trigger == "initialize")
			{
				string probePath = Path.Combine(
					this.Manifest.RootDirectoryPath,
					"coi_machine_buffer_probe.json"
				);

				string probeJson = BuildMachineBufferProbeJson(machines);

				File.WriteAllText(probePath, probeJson);

				Log.Info("CLExporter: wrote machine buffer probe to " + probePath);

				string powerFarmProbePath = Path.Combine(
				this.Manifest.RootDirectoryPath,
				"coi_power_farm_type_probe.json"
			);

				string powerFarmProbeJson = BuildPowerFarmTypeProbeJson();

				File.WriteAllText(powerFarmProbePath, powerFarmProbeJson);

				Log.Info("CLExporter: wrote power/farm type probe to " + powerFarmProbePath);

				string powerFarmLiveProbePath = Path.Combine(
					this.Manifest.RootDirectoryPath,
					"coi_power_farm_live_probe.json"
				);

				string powerFarmLiveProbeJson = BuildPowerFarmLiveProbeJson(
					m_entitiesManager,
					m_resolver
				);

				string powerEntityProbePath = Path.Combine(
					this.Manifest.RootDirectoryPath,
					"coi_power_entity_probe.json"
				);

				string powerEntityProbeJson = BuildPowerEntityProbeJson(m_entitiesManager);

				File.WriteAllText(powerEntityProbePath, powerEntityProbeJson);

				Log.Info("CLExporter: wrote power entity probe to " + powerEntityProbePath);

				File.WriteAllText(powerFarmLiveProbePath, powerFarmLiveProbeJson);

				Log.Info("CLExporter: wrote power/farm live probe to " + powerFarmLiveProbePath);
			}
		}
	}

	private void WriteWorldMachinesError(string trigger, Exception ex)
	{
		string outputPath = Path.Combine(
			this.Manifest.RootDirectoryPath,
			"coi_world_machines.json"
		);

		string json =
			"{\n" +
			"  \"exportType\": \"worldMachines\",\n" +
			"  \"success\": false,\n" +
			"  \"trigger\": \"" + EscapeJson(trigger) + "\",\n" +
			"  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"\n" +
			"}\n";

		File.WriteAllText(outputPath, json);

		Log.Info("CLExporter: wrote world machines error export to " + outputPath);
	}

	private static System.Collections.Generic.List<string> GetAssignedRecipeIds(Machine machine)
	{
		var result = new System.Collections.Generic.List<string>();

		try
		{
			object assignedRecipes = machine.RecipesAssigned;

			if (assignedRecipes == null)
			{
				return result;
			}

			Type type = assignedRecipes.GetType();

			var countProp =
				type.GetProperty("Count") ??
				type.GetProperty("Length");

			var indexerProp = type
				.GetProperties()
				.FirstOrDefault(p => p.GetIndexParameters().Length == 1);

			if (countProp != null && indexerProp != null)
			{
				int count = Convert.ToInt32(countProp.GetValue(assignedRecipes, null));

				for (int i = 0; i < count; i++)
				{
					object recipe = indexerProp.GetValue(assignedRecipes, new object[] { i });
					string recipeId = GetProtoId(recipe);

					if (!string.IsNullOrEmpty(recipeId))
					{
						result.Add(recipeId);
					}
				}

				return result;
			}

			if (assignedRecipes is System.Collections.IEnumerable enumerable)
			{
				foreach (object recipe in enumerable)
				{
					string recipeId = GetProtoId(recipe);

					if (!string.IsNullOrEmpty(recipeId))
					{
						result.Add(recipeId);
					}
				}
			}
		}
		catch (Exception ex)
		{
			result.Add("ERROR: " + ex.GetType().Name + ": " + ex.Message);
		}

		return result;
	}

	private static string GetProtoId(object protoLikeObject)
	{
		if (protoLikeObject == null)
		{
			return "";
		}

		try
		{
			Type type = protoLikeObject.GetType();

			var idProp = type.GetProperty("Id");

			if (idProp != null)
			{
				object id = idProp.GetValue(protoLikeObject, null);

				if (id != null)
				{
					return CleanRecipeId(id.ToString());
				}
			}

			return CleanRecipeId(protoLikeObject.ToString());
		}
		catch
		{
			return CleanRecipeId(protoLikeObject.ToString());
		}
	}

	private static string CleanRecipeId(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "";
		}

		value = value.Trim();

		if (value.StartsWith("Some: "))
		{
			value = value.Substring("Some: ".Length).Trim();
		}

		if (value.StartsWith("None: "))
		{
			return "";
		}

		if (value.EndsWith(" (recipe)"))
		{
			value = value.Substring(0, value.Length - " (recipe)".Length).Trim();
		}

		return value;
	}

	private void StartRefreshTimer()
	{
		if (m_refreshTimer != null)
		{
			return;
		}

		m_refreshTimer = new System.Timers.Timer(REFRESH_INTERVAL_MS);
		m_refreshTimer.AutoReset = true;

		m_refreshTimer.Elapsed += (sender, args) =>
		{
			try
			{
				if (!IsActiveExporterInstance())
				{
					Log.Info(
						"CLExporter: stopping inactive timer for instance " +
						m_instanceId +
						". Active instance is " +
						System.Threading.Volatile.Read(ref s_activeInstanceId)
					);

					try
					{
						m_refreshTimer.Stop();
						m_refreshTimer.Dispose();
					}
					catch
					{
						// Timer cleanup safety.
					}

					m_refreshTimer = null;
					return;
				}

				ExportWorldMachines(true, "timer");
			}
			catch (Exception ex)
			{
				WriteWorldMachinesError("timer", ex);
			}
		};

		m_refreshTimer.Start();

		Log.Info("CLExporter: refresh timer started. IntervalMs=" + REFRESH_INTERVAL_MS);
	}

	private static string BuildMachineBufferProbeJson(System.Collections.Generic.List<Machine> machines)
	{
		var sb = new StringBuilder();

		sb.AppendLine("{");
		sb.AppendLine("  \"exportType\": \"machineBufferProbe\",");
		sb.AppendLine("  \"machineCount\": " + machines.Count + ",");
		sb.AppendLine("  \"machines\": [");

		int maxMachines = Math.Min(machines.Count, 8);

		for (int i = 0; i < maxMachines; i++)
		{
			var machine = machines[i];

			sb.AppendLine("    {");
			sb.AppendLine("      \"entityId\": \"" + EscapeJson(machine.Id.ToString()) + "\",");
			sb.AppendLine("      \"machineId\": \"" + EscapeJson(machine.Prototype.Id.ToString()) + "\",");
			sb.AppendLine("      \"title\": \"" + EscapeJson(machine.DefaultTitle.ToString()) + "\",");
			sb.AppendLine("      \"currentState\": \"" + EscapeJson(machine.CurrentState.ToString()) + "\",");
			sb.AppendLine("      \"utilization\": \"" + EscapeJson(machine.Utilization.ToString()) + "\",");

			sb.AppendLine("      \"privateFields\": {");

			WritePrivateFieldProbe(sb, machine, "m_inputBuffers", 8);
			sb.AppendLine(",");

			WritePrivateFieldProbe(sb, machine, "m_outputBuffers", 8);
			sb.AppendLine(",");

			WritePrivateFieldProbe(sb, machine, "m_productInNeedOfTransport", 8);
			sb.AppendLine(",");

			WritePrivateFieldProbe(sb, machine, "m_recipeResult", 8);

			sb.AppendLine();
			sb.AppendLine("      }");
			sb.Append("    }");

			if (i < maxMachines - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.AppendLine("  ]");
		sb.AppendLine("}");

		return sb.ToString();
	}
	private static void WritePrivateFieldProbe(StringBuilder sb, object target, string fieldName, int indent)
	{
		string pad = new string(' ', indent);

		try
		{
			Type targetType = target.GetType();

			var field = targetType.GetField(
				fieldName,
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
			);

			sb.AppendLine(pad + "\"" + EscapeJson(fieldName) + "\": {");

			if (field == null)
			{
				sb.AppendLine(pad + "  \"found\": false");
				sb.Append(pad + "}");
				return;
			}

			object value = field.GetValue(target);

			sb.AppendLine(pad + "  \"found\": true,");
			sb.AppendLine(pad + "  \"fieldType\": \"" + EscapeJson(field.FieldType.FullName) + "\",");

			if (value == null)
			{
				sb.AppendLine(pad + "  \"isNull\": true");
				sb.Append(pad + "}");
				return;
			}

			Type valueType = value.GetType();

			sb.AppendLine(pad + "  \"isNull\": false,");
			sb.AppendLine(pad + "  \"runtimeType\": \"" + EscapeJson(valueType.FullName) + "\",");
			sb.AppendLine(pad + "  \"toString\": \"" + EscapeJson(value.ToString()) + "\",");

			var properties = valueType
				.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
				.Select(p => p.PropertyType.FullName + " " + p.Name)
				.OrderBy(x => x)
				.Take(120)
				.ToList();

			var fields = valueType
				.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
				.Select(f => f.FieldType.FullName + " " + f.Name)
				.OrderBy(x => x)
				.Take(120)
				.ToList();

			var methods = valueType
				.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
				.Select(m =>
					m.ReturnType.FullName + " " +
					m.Name + "(" +
					string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name)) +
					")"
				)
				.Distinct()
				.OrderBy(x => x)
				.Take(160)
				.ToList();

			WriteStringArray(sb, "properties", properties, indent + 2);
			sb.AppendLine(",");

			WriteStringArray(sb, "fields", fields, indent + 2);
			sb.AppendLine(",");

			WriteStringArray(sb, "methods", methods, indent + 2);
			sb.AppendLine(",");

			WriteEnumerableItemsProbe(sb, value, "items", indent + 2);
			sb.AppendLine();

			sb.Append(pad + "}");
		}
		catch (Exception ex)
		{
			sb.AppendLine(pad + "\"" + EscapeJson(fieldName) + "\": {");
			sb.AppendLine(pad + "  \"found\": false,");
			sb.AppendLine(pad + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
			sb.Append(pad + "}");
		}
	}
	private static void WriteEnumerableItemsProbe(StringBuilder sb, object value, string name, int indent)
	{
		string pad = new string(' ', indent);

		var itemProbes = new System.Collections.Generic.List<string>();

		if (value != null)
		{
			Type valueType = value.GetType();

			var countProp = valueType.GetProperty(
				"Count",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
			);

			var indexerProp = valueType
				.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
				.FirstOrDefault(p =>
					p.Name == "Item" &&
					p.GetIndexParameters().Length == 1
				);

			if (countProp != null && indexerProp != null)
			{
				int count = Convert.ToInt32(countProp.GetValue(value, null));
				int maxItems = Math.Min(count, 20);

				for (int i = 0; i < maxItems; i++)
				{
					try
					{
						object item = indexerProp.GetValue(value, new object[] { i });
						itemProbes.Add(BuildObjectMiniProbeString(item));
					}
					catch (Exception ex)
					{
						itemProbes.Add("{ \"index\": " + i + ", \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\" }");
					}
				}

				if (count > maxItems)
				{
					itemProbes.Add("{ \"truncated\": true }");
				}
			}
			else if (value is System.Collections.IEnumerable enumerable)
			{
				int index = 0;

				foreach (object item in enumerable)
				{
					if (index >= 20)
					{
						itemProbes.Add("{ \"truncated\": true }");
						break;
					}

					itemProbes.Add(BuildObjectMiniProbeString(item));
					index++;
				}
			}
			else
			{
				itemProbes.Add("{ \"isEnumerable\": false, \"hasCountIndexer\": false }");
			}
		}
		else
		{
			itemProbes.Add("{ \"isNull\": true }");
		}

		sb.AppendLine(pad + "\"" + EscapeJson(name) + "\": [");

		for (int i = 0; i < itemProbes.Count; i++)
		{
			sb.Append(pad + "  " + itemProbes[i]);

			if (i < itemProbes.Count - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.Append(pad + "]");
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

			var sb = new StringBuilder();

			sb.Append("{ ");
			sb.Append("\"isNull\": false, ");
			sb.Append("\"runtimeType\": \"" + EscapeJson(type.FullName) + "\", ");
			sb.Append("\"toString\": \"" + EscapeJson(item.ToString()) + "\", ");

			sb.Append("\"properties\": [");

			var properties = type
				.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
				.OrderBy(p => p.Name)
				.Take(40)
				.ToList();

			for (int i = 0; i < properties.Count; i++)
			{
				var p = properties[i];

				sb.Append("{ ");
				sb.Append("\"name\": \"" + EscapeJson(p.Name) + "\", ");
				sb.Append("\"type\": \"" + EscapeJson(p.PropertyType.FullName) + "\"");

				if (p.GetIndexParameters().Length == 0)
				{
					try
					{
						object v = p.GetValue(item, null);
						sb.Append(", \"value\": \"" + EscapeJson(v == null ? "" : v.ToString()) + "\"");
					}
					catch (Exception ex)
					{
						sb.Append(", \"valueError\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
					}
				}

				sb.Append(" }");

				if (i < properties.Count - 1)
				{
					sb.Append(", ");
				}
			}

			sb.Append("], ");

			sb.Append("\"fields\": [");

			var fields = type
				.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
				.OrderBy(f => f.Name)
				.Take(40)
				.ToList();

			for (int i = 0; i < fields.Count; i++)
			{
				var f = fields[i];

				sb.Append("{ ");
				sb.Append("\"name\": \"" + EscapeJson(f.Name) + "\", ");
				sb.Append("\"type\": \"" + EscapeJson(f.FieldType.FullName) + "\"");

				try
				{
					object v = f.GetValue(item);
					sb.Append(", \"value\": \"" + EscapeJson(v == null ? "" : v.ToString()) + "\"");
				}
				catch (Exception ex)
				{
					sb.Append(", \"valueError\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
				}

				sb.Append(" }");

				if (i < fields.Count - 1)
				{
					sb.Append(", ");
				}
			}

			sb.Append("]");

			sb.Append(" }");

			return sb.ToString();
		}
		catch (Exception ex)
		{
			return "{ \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\" }";
		}
	}

	private static void WriteMachineBufferArray(
		StringBuilder sb,
		string jsonName,
		Machine machine,
		string privateFieldName,
		int indent
	)
	{
		string pad = new string(' ', indent);

		sb.AppendLine(pad + "\"" + EscapeJson(jsonName) + "\": [");

		var buffers = GetMachineBuffers(machine, privateFieldName);

		for (int i = 0; i < buffers.Count; i++)
		{
			object buffer = buffers[i];

			sb.AppendLine(pad + "  {");
			sb.AppendLine(pad + "    \"productId\": \"" + EscapeJson(GetProductIdFromBuffer(buffer)) + "\",");
			sb.AppendLine(pad + "    \"quantity\": \"" + EscapeJson(GetPropertyString(buffer, "Quantity")) + "\",");
			sb.AppendLine(pad + "    \"capacity\": \"" + EscapeJson(GetPropertyString(buffer, "Capacity")) + "\",");
			sb.AppendLine(pad + "    \"usableCapacity\": \"" + EscapeJson(GetPropertyString(buffer, "UsableCapacity")) + "\",");
			sb.AppendLine(pad + "    \"isFull\": " + GetPropertyBoolJson(buffer, "IsFull") + ",");
			sb.AppendLine(pad + "    \"isEmpty\": " + GetPropertyBoolJson(buffer, "IsEmpty") + ",");
			sb.AppendLine(pad + "    \"isNotUsedByCurrentRecipes\": " + GetPropertyBoolJson(buffer, "IsNotUsedByCurrentRecipes") + ",");
			sb.AppendLine(pad + "    \"isAnyPortConnected\": " + GetPropertyBoolJson(buffer, "IsAnyPortConnected") + ",");
			sb.AppendLine(pad + "    \"isRegisteredToLogistics\": " + GetPropertyBoolJson(buffer, "IsRegisteredToLogistics"));
			sb.Append(pad + "  }");

			if (i < buffers.Count - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.Append(pad + "]");
	}

	private static System.Collections.Generic.List<object> GetMachineBuffers(Machine machine, string privateFieldName)
	{
		var result = new System.Collections.Generic.List<object>();

		try
		{
			var field = machine.GetType().GetField(
				privateFieldName,
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
			);

			if (field == null)
			{
				return result;
			}

			object value = field.GetValue(machine);

			if (value == null)
			{
				return result;
			}

			Type valueType = value.GetType();

			var countProp = valueType.GetProperty(
				"Count",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
			);

			var indexerProp = valueType
				.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
				.FirstOrDefault(p =>
					p.Name == "Item" &&
					p.GetIndexParameters().Length == 1
				);

			if (countProp == null || indexerProp == null)
			{
				return result;
			}

			int count = Convert.ToInt32(countProp.GetValue(value, null));

			for (int i = 0; i < count; i++)
			{
				object buffer = indexerProp.GetValue(value, new object[] { i });

				if (buffer != null)
				{
					result.Add(buffer);
				}
			}
		}
		catch
		{
			// Keep export safe. Empty buffers are better than killing the mod.
		}

		return result;
	}

	private static string GetProductIdFromBuffer(object buffer)
	{
		string product = GetPropertyString(buffer, "Product");

		if (string.IsNullOrWhiteSpace(product))
		{
			return "";
		}

		product = product.Trim();

		int firstSpace = product.IndexOf(" ");

		if (firstSpace > 0)
		{
			return product.Substring(0, firstSpace);
		}

		return product;
	}

	private static string GetPropertyString(object target, string propertyName)
	{
		try
		{
			if (target == null)
			{
				return "";
			}

			var prop = target.GetType().GetProperty(
				propertyName,
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
			);

			if (prop == null || prop.GetIndexParameters().Length > 0)
			{
				return "";
			}

			object value = prop.GetValue(target, null);

			return value == null ? "" : value.ToString();
		}
		catch
		{
			return "";
		}
	}

	private static string GetPropertyBoolJson(object target, string propertyName)
	{
		string value = GetPropertyString(target, propertyName);

		if (string.Equals(value, "True", StringComparison.OrdinalIgnoreCase))
		{
			return "true";
		}

		if (string.Equals(value, "False", StringComparison.OrdinalIgnoreCase))
		{
			return "false";
		}

		return "false";
	}


	private static string BuildPowerFarmLiveProbeJson(IEntitiesManager entitiesManager, DependencyResolver resolver)
	{
		var entityTypeNames = new[] {
		"Mafi.Core.Buildings.Farms.Farm",
		"Mafi.Core.Buildings.Farms.AnimalFarm",

		"Mafi.Base.Prototypes.Machines.PowerGenerators.ElectricityGeneratorFromProduct",
		"Mafi.Base.Prototypes.Machines.PowerGenerators.ElectricityGeneratorFromMechPower",
		"Mafi.Base.Prototypes.Machines.PowerGenerators.MechPowerGeneratorFromProduct",
		"Mafi.Base.Prototypes.Machines.PowerGenerators.FlyWheelEntity",
		"Mafi.Base.Prototypes.Machines.SolarElectricityGenerator"
	};

		var managerTypeNames = new[] {
		"Mafi.Core.Factory.ElectricPower.IElectricityManager",
		"Mafi.Core.Factory.ElectricPower.ElectricityManager",
		"Mafi.Core.Factory.ElectricPower.ISolarPanelsManager",
		"Mafi.Core.Factory.ElectricPower.SolarPanelsManager"
	};

		var sb = new StringBuilder();

		sb.AppendLine("{");
		sb.AppendLine("  \"exportType\": \"powerFarmLiveProbe\",");
		sb.AppendLine("  \"entityTypeProbes\": [");

		for (int i = 0; i < entityTypeNames.Length; i++)
		{
			WriteLiveEntityTypeProbe(sb, entitiesManager, entityTypeNames[i], 4);

			if (i < entityTypeNames.Length - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.AppendLine("  ],");
		sb.AppendLine("  \"managerResolveProbes\": [");

		for (int i = 0; i < managerTypeNames.Length; i++)
		{
			WriteResolverTypeProbe(sb, resolver, managerTypeNames[i], 4);

			if (i < managerTypeNames.Length - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.AppendLine("  ]");
		sb.AppendLine("}");

		return sb.ToString();
	}

	private static void WriteLiveEntityTypeProbe(
		StringBuilder sb,
		IEntitiesManager entitiesManager,
		string typeName,
		int indent
	)
	{
		string pad = new string(' ', indent);

		sb.AppendLine(pad + "{");
		sb.AppendLine(pad + "  \"typeName\": \"" + EscapeJson(typeName) + "\",");

		try
		{
			Type type = FindTypeByFullName(typeName);

			if (type == null)
			{
				sb.AppendLine(pad + "  \"typeFound\": false");
				sb.Append(pad + "}");
				return;
			}

			var entities = GetAllEntitiesOfTypeReflective(entitiesManager, type);

			sb.AppendLine(pad + "  \"typeFound\": true,");
			sb.AppendLine(pad + "  \"assembly\": \"" + EscapeJson(type.Assembly.GetName().Name) + "\",");
			sb.AppendLine(pad + "  \"entityCount\": " + entities.Count + ",");

			WriteTypeMemberSummary(sb, type, "typeMembers", indent + 2);
			sb.AppendLine(",");

			sb.AppendLine(pad + "  \"sampleEntities\": [");

			int max = Math.Min(entities.Count, 5);

			for (int i = 0; i < max; i++)
			{
				sb.Append(pad + "    " + BuildObjectMiniProbeString(entities[i]));

				if (i < max - 1)
				{
					sb.Append(",");
				}

				sb.AppendLine();
			}

			sb.AppendLine(pad + "  ]");
			sb.Append(pad + "}");
		}
		catch (Exception ex)
		{
			sb.AppendLine(pad + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
			sb.Append(pad + "}");
		}
	}

	private static System.Collections.Generic.List<object> GetAllEntitiesOfTypeReflective(
		IEntitiesManager entitiesManager,
		Type entityType
	)
	{
		var result = new System.Collections.Generic.List<object>();

		try
		{
			var method = entitiesManager
				.GetType()
				.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
				.FirstOrDefault(m =>
					m.Name == "GetAllEntitiesOfType" &&
					m.IsGenericMethodDefinition &&
					m.GetParameters().Length == 0
				);

			if (method == null)
			{
				return result;
			}

			var genericMethod = method.MakeGenericMethod(entityType);
			object value = genericMethod.Invoke(entitiesManager, null);

			AddObjectsFromCollection(result, value);
		}
		catch
		{
			// Probe safety. Empty result is better than killing the mod.
		}

		return result;
	}

	private static void WriteResolverTypeProbe(
		StringBuilder sb,
		DependencyResolver resolver,
		string typeName,
		int indent
	)
	{
		string pad = new string(' ', indent);

		sb.AppendLine(pad + "{");
		sb.AppendLine(pad + "  \"typeName\": \"" + EscapeJson(typeName) + "\",");

		try
		{
			Type type = FindTypeByFullName(typeName);

			if (type == null)
			{
				sb.AppendLine(pad + "  \"typeFound\": false");
				sb.Append(pad + "}");
				return;
			}

			object resolved = ResolveTypeReflective(resolver, type);

			sb.AppendLine(pad + "  \"typeFound\": true,");
			sb.AppendLine(pad + "  \"resolved\": " + (resolved != null ? "true" : "false") + ",");

			if (resolved == null)
			{
				WriteTypeMemberSummary(sb, type, "typeMembers", indent + 2);
				sb.AppendLine();
				sb.Append(pad + "}");
				return;
			}

			Type runtimeType = resolved.GetType();

			sb.AppendLine(pad + "  \"runtimeType\": \"" + EscapeJson(runtimeType.FullName) + "\",");
			sb.AppendLine(pad + "  \"toString\": \"" + EscapeJson(resolved.ToString()) + "\",");

			WriteTypeMemberSummary(sb, runtimeType, "runtimeMembers", indent + 2);
			sb.AppendLine(",");

			sb.AppendLine(pad + "  \"objectProbe\": " + BuildObjectMiniProbeString(resolved));

			sb.Append(pad + "}");
		}
		catch (Exception ex)
		{
			sb.AppendLine(pad + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
			sb.Append(pad + "}");
		}
	}

	private static object ResolveTypeReflective(DependencyResolver resolver, Type type)
	{
		try
		{
			var method = resolver
				.GetType()
				.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
				.FirstOrDefault(m =>
					m.Name == "Resolve" &&
					m.IsGenericMethodDefinition &&
					m.GetParameters().Length == 0
				);

			if (method == null)
			{
				return null;
			}

			var genericMethod = method.MakeGenericMethod(type);

			return genericMethod.Invoke(resolver, null);
		}
		catch
		{
			return null;
		}
	}

	private static Type FindTypeByFullName(string fullName)
	{
		return AppDomain.CurrentDomain
			.GetAssemblies()
			.SelectMany(a =>
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
			.FirstOrDefault(t => t.FullName == fullName);
	}

	private static void AddObjectsFromCollection(System.Collections.Generic.List<object> result, object value)
	{
		if (value == null)
		{
			return;
		}

		Type valueType = value.GetType();

		var countProp = valueType.GetProperty(
			"Count",
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
		);

		var indexerProp = valueType
			.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
			.FirstOrDefault(p =>
				p.Name == "Item" &&
				p.GetIndexParameters().Length == 1
			);

		if (countProp != null && indexerProp != null)
		{
			int count = Convert.ToInt32(countProp.GetValue(value, null));

			for (int i = 0; i < count; i++)
			{
				object item = indexerProp.GetValue(value, new object[] { i });

				if (item != null)
				{
					result.Add(item);
				}
			}

			return;
		}

		if (value is System.Collections.IEnumerable enumerable)
		{
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
		string pad = new string(' ', indent);

		sb.AppendLine(pad + "\"" + EscapeJson(jsonName) + "\": {");
		sb.AppendLine(pad + "  \"baseType\": \"" + EscapeJson(type.BaseType == null ? "" : type.BaseType.FullName) + "\",");

		var properties = type
			.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
			.Select(p => p.PropertyType.FullName + " " + p.Name)
			.OrderBy(x => x)
			.Take(120)
			.ToList();

		var fields = type
			.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
			.Select(f => f.FieldType.FullName + " " + f.Name)
			.OrderBy(x => x)
			.Take(120)
			.ToList();

		var methods = type
			.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
			.Select(m =>
				m.ReturnType.FullName + " " +
				m.Name + "(" +
				string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name)) +
				")"
			)
			.Distinct()
			.OrderBy(x => x)
			.Take(160)
			.ToList();

		WriteStringArray(sb, "properties", properties, indent + 2);
		sb.AppendLine(",");

		WriteStringArray(sb, "fields", fields, indent + 2);
		sb.AppendLine(",");

		WriteStringArray(sb, "methods", methods, indent + 2);
		sb.AppendLine();

		sb.Append(pad + "}");
	}

	private static string BuildPowerJson(object electricityManager)
	{
		var sb = new StringBuilder();

		sb.AppendLine("{");
		sb.AppendLine("  \"exportType\": \"power\",");
		sb.AppendLine("  \"success\": " + (electricityManager != null ? "true" : "false") + ",");

		if (electricityManager == null)
		{
			sb.AppendLine("  \"error\": \"IElectricityManager was not resolved\"");
			sb.AppendLine("}");
			return sb.ToString();
		}

		sb.AppendLine("  \"runtimeType\": \"" + EscapeJson(electricityManager.GetType().FullName) + "\",");
		sb.AppendLine("  \"consumedThisTick\": \"" + EscapeJson(GetPropertyString(electricityManager, "ConsumedThisTick")) + "\",");
		sb.AppendLine("  \"demandedThisTick\": \"" + EscapeJson(GetPropertyString(electricityManager, "DemandedThisTick")) + "\",");
		sb.AppendLine("  \"generatedThisTick\": \"" + EscapeJson(GetPropertyString(electricityManager, "GeneratedThisTick")) + "\",");
		sb.AppendLine("  \"generationCapacityThisTick\": \"" + EscapeJson(GetPropertyString(electricityManager, "GenerationCapacityThisTick")) + "\",");
		sb.AppendLine("  \"maxGenerationCapacity\": \"" + EscapeJson(GetPropertyString(electricityManager, "MaxGenerationCapacity")) + "\",");
		sb.AppendLine("  \"wastedElectricityThisTick\": \"" + EscapeJson(GetPropertyString(electricityManager, "WastedElectricityThisTick")) + "\",");
		sb.AppendLine("  \"generatorsCount\": \"" + EscapeJson(GetPropertyString(electricityManager, "GeneratorsCount")) + "\",");
		sb.AppendLine("  \"consumersCount\": \"" + EscapeJson(GetPropertyString(electricityManager, "ConsumersCount")) + "\"");
		sb.AppendLine("}");

		return sb.ToString();
	}

	private static string BuildPowerEntityProbeJson(IEntitiesManager entitiesManager)
	{
		var entityTypeNames = new[] {
		"Mafi.Base.Prototypes.Machines.PowerGenerators.ElectricityGeneratorFromProduct",
		"Mafi.Base.Prototypes.Machines.PowerGenerators.ElectricityGeneratorFromMechPower",
		"Mafi.Base.Prototypes.Machines.PowerGenerators.MechPowerGeneratorFromProduct",
		"Mafi.Base.Prototypes.Machines.PowerGenerators.FlyWheelEntity",
		"Mafi.Base.Prototypes.Machines.SolarElectricityGenerator"
	};

		var sb = new StringBuilder();

		sb.AppendLine("{");
		sb.AppendLine("  \"exportType\": \"powerEntityProbe\",");
		sb.AppendLine("  \"entityTypeProbes\": [");

		for (int i = 0; i < entityTypeNames.Length; i++)
		{
			WritePowerEntityTypeProbe(sb, entitiesManager, entityTypeNames[i], 4);

			if (i < entityTypeNames.Length - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.AppendLine("  ]");
		sb.AppendLine("}");

		return sb.ToString();
	}
	private static void WritePowerEntityTypeProbe(
		StringBuilder sb,
		IEntitiesManager entitiesManager,
		string typeName,
		int indent
	)
	{
		string pad = new string(' ', indent);

		sb.AppendLine(pad + "{");
		sb.AppendLine(pad + "  \"typeName\": \"" + EscapeJson(typeName) + "\",");

		try
		{
			Type type = FindTypeByFullName(typeName);

			if (type == null)
			{
				sb.AppendLine(pad + "  \"typeFound\": false");
				sb.Append(pad + "}");
				return;
			}

			var entities = GetAllEntitiesOfTypeReflective(entitiesManager, type);

			sb.AppendLine(pad + "  \"typeFound\": true,");
			sb.AppendLine(pad + "  \"assembly\": \"" + EscapeJson(type.Assembly.GetName().Name) + "\",");
			sb.AppendLine(pad + "  \"entityCount\": " + entities.Count + ",");

			WriteTypeMemberSummary(sb, type, "typeMembers", indent + 2);
			sb.AppendLine(",");

			sb.AppendLine(pad + "  \"sampleEntities\": [");

			int max = Math.Min(entities.Count, 8);

			for (int i = 0; i < max; i++)
			{
				object entity = entities[i];

				WritePowerEntityObjectProbe(sb, entity, indent + 4);

				if (i < max - 1)
				{
					sb.Append(",");
				}

				sb.AppendLine();
			}

			sb.AppendLine(pad + "  ]");
			sb.Append(pad + "}");
		}
		catch (Exception ex)
		{
			sb.AppendLine(pad + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
			sb.Append(pad + "}");
		}
	}
	private static void WritePowerEntityObjectProbe(StringBuilder sb, object entity, int indent)
	{
		string pad = new string(' ', indent);

		sb.AppendLine(pad + "{");

		if (entity == null)
		{
			sb.AppendLine(pad + "  \"isNull\": true");
			sb.Append(pad + "}");
			return;
		}

		try
		{
			Type type = entity.GetType();

			sb.AppendLine(pad + "  \"isNull\": false,");
			sb.AppendLine(pad + "  \"runtimeType\": \"" + EscapeJson(type.FullName) + "\",");
			sb.AppendLine(pad + "  \"toString\": \"" + EscapeJson(entity.ToString()) + "\",");

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

			sb.AppendLine(pad + "  \"privateFieldProbes\": {");

			string[] privateFields = new[] {
			"m_electricityGenerator",
			"m_electricityConsumer",
			"m_generator",
			"m_consumer",
			"m_powerGenerator",
			"m_powerConsumer",
			"m_inputBuffers",
			"m_outputBuffers",
			"m_productBuffer",
			"m_productConsumer",
			"m_state",
			"m_proto"
		};

			for (int i = 0; i < privateFields.Length; i++)
			{
				WritePrivateFieldProbe(sb, entity, privateFields[i], indent + 4);

				if (i < privateFields.Length - 1)
				{
					sb.Append(",");
				}

				sb.AppendLine();
			}

			sb.AppendLine(pad + "  },");
			sb.AppendLine(pad + "  \"publicBufferCollections\": {");

			WritePropertyObjectProbe(sb, entity, "InputBuffers", indent + 4);
			sb.AppendLine(",");

			WritePropertyObjectProbe(sb, entity, "OutputBuffers", indent + 4);

			sb.AppendLine();
			sb.AppendLine(pad + "  }");

			sb.Append(pad + "}");
		}
		catch (Exception ex)
		{
			sb.AppendLine(pad + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
			sb.Append(pad + "}");
		}
	}
	private static void WriteSimplePropertyValue(StringBuilder sb, object target, string propertyName, int indent)
	{
		string pad = new string(' ', indent);

		try
		{
			if (target == null)
			{
				sb.Append(pad + "\"" + EscapeJson(propertyName) + "\": { \"found\": false, \"isNull\": true }");
				return;
			}

			var prop = target.GetType().GetProperty(
				propertyName,
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
			);

			if (prop == null || prop.GetIndexParameters().Length > 0)
			{
				sb.Append(pad + "\"" + EscapeJson(propertyName) + "\": { \"found\": false }");
				return;
			}

			object value = prop.GetValue(target, null);

			sb.Append(pad + "\"" + EscapeJson(propertyName) + "\": { ");
			sb.Append("\"found\": true, ");
			sb.Append("\"type\": \"" + EscapeJson(prop.PropertyType.FullName) + "\", ");
			sb.Append("\"value\": \"" + EscapeJson(value == null ? "" : value.ToString()) + "\"");
			sb.Append(" }");
		}
		catch (Exception ex)
		{
			sb.Append(pad + "\"" + EscapeJson(propertyName) + "\": { ");
			sb.Append("\"found\": false, ");
			sb.Append("\"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
			sb.Append(" }");
		}
	}
	private static void WritePropertyObjectProbe(StringBuilder sb, object target, string propertyName, int indent)
	{
		string pad = new string(' ', indent);

		sb.AppendLine(pad + "\"" + EscapeJson(propertyName) + "\": {");

		try
		{
			if (target == null)
			{
				sb.AppendLine(pad + "  \"found\": false,");
				sb.AppendLine(pad + "  \"isNull\": true");
				sb.Append(pad + "}");
				return;
			}

			var prop = target.GetType().GetProperty(
				propertyName,
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
			);

			if (prop == null || prop.GetIndexParameters().Length > 0)
			{
				sb.AppendLine(pad + "  \"found\": false");
				sb.Append(pad + "}");
				return;
			}

			object value = prop.GetValue(target, null);

			sb.AppendLine(pad + "  \"found\": true,");
			sb.AppendLine(pad + "  \"propertyType\": \"" + EscapeJson(prop.PropertyType.FullName) + "\",");

			if (value == null)
			{
				sb.AppendLine(pad + "  \"isNull\": true");
				sb.Append(pad + "}");
				return;
			}

			sb.AppendLine(pad + "  \"isNull\": false,");
			sb.AppendLine(pad + "  \"runtimeType\": \"" + EscapeJson(value.GetType().FullName) + "\",");
			sb.AppendLine(pad + "  \"toString\": \"" + EscapeJson(value.ToString()) + "\",");

			WriteEnumerableItemsProbe(sb, value, "items", indent + 2);
			sb.AppendLine();

			sb.Append(pad + "}");
		}
		catch (Exception ex)
		{
			sb.AppendLine(pad + "  \"found\": false,");
			sb.AppendLine(pad + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
			sb.Append(pad + "}");
		}
	}
	private static string BuildPowerEntitiesJson(IEntitiesManager entitiesManager)
	{
		var generatorTypeNames = new[] {
		"Mafi.Base.Prototypes.Machines.PowerGenerators.ElectricityGeneratorFromProduct",
		"Mafi.Base.Prototypes.Machines.PowerGenerators.ElectricityGeneratorFromMechPower",
		"Mafi.Base.Prototypes.Machines.PowerGenerators.MechPowerGeneratorFromProduct",
		"Mafi.Base.Prototypes.Machines.PowerGenerators.FlyWheelEntity",
		"Mafi.Base.Prototypes.Machines.SolarElectricityGenerator"
	};

		var generators = new System.Collections.Generic.List<object>();

		foreach (string typeName in generatorTypeNames)
		{
			Type type = FindTypeByFullName(typeName);

			if (type == null)
			{
				continue;
			}

			var entities = GetAllEntitiesOfTypeReflective(entitiesManager, type);

			foreach (object entity in entities)
			{
				if (entity != null)
				{
					generators.Add(entity);
				}
			}
		}

		var sb = new StringBuilder();

		sb.AppendLine("{");
		sb.AppendLine("  \"exportType\": \"powerEntities\",");
		sb.AppendLine("  \"success\": true,");
		sb.AppendLine("  \"generatorCount\": " + generators.Count + ",");
		sb.AppendLine("  \"generators\": [");

		for (int i = 0; i < generators.Count; i++)
		{
			WritePowerGeneratorJson(sb, generators[i], 4);

			if (i < generators.Count - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.AppendLine("  ]");
		sb.AppendLine("}");

		return sb.ToString();
	}
	private static void WritePowerGeneratorJson(StringBuilder sb, object entity, int indent)
	{
		string pad = new string(' ', indent);

		Type type = entity.GetType();

		string entityId = GetPropertyString(entity, "Id");
		string title = GetPropertyString(entity, "DefaultTitle");
		string customTitleRaw = GetPropertyString(entity, "CustomTitle");
		string customTitle = CleanOptionString(customTitleRaw);
		string displayTitle = string.IsNullOrWhiteSpace(customTitle) ? title : customTitle;
		string position = GetPropertyString(entity, "Position3f");

		string isEnabled = GetPropertyBoolJson(entity, "IsEnabled");
		string isPaused = GetPropertyBoolJson(entity, "IsPaused");
		string isEnabledNow = GetPropertyBoolJson(entity, "IsEnabledNow");

		string currentState = GetPropertyString(entity, "CurrentState");
		string state = GetPropertyString(entity, "State");

		if (string.IsNullOrWhiteSpace(currentState))
		{
			currentState = state;
		}

		string maxGenerationCapacity = GetPropertyString(entity, "MaxGenerationCapacity");
		string generatedElectricityThisTick = GetPropertyString(entity, "GeneratedElectricityThisTick");
		string usedMechPowerThisTick = GetPropertyString(entity, "UsedMechPowerThisTick");

		// Solar-specific
		string maxOutputElectricity = GetPropertyString(entity, "MaxOutputElectricity");

		// Turbine / mechanical source-specific
		string powerGeneratedLastTick = GetPropertyString(entity, "PowerGeneratedLastTick");
		string inputBufferQuantity = GetPropertyString(entity, "InputBufferQuantity");
		string outputBufferQuantity = GetPropertyString(entity, "OutputBufferQuantity");

		string currentFuelUsage = GetPropertyString(entity, "CurrentFuelUsage");
		string currentProductLeft = GetPropertyString(entity, "CurrentProductLeft");
		string currentProgress = GetPropertyString(entity, "CurrentProgress");

		object proto = GetPrivateFieldValue(entity, "m_proto");

		string protoId = "";
		string outputElectricity = "";
		string duration = "";
		string inputProduct = "";
		string outputProduct = "";

		// Solar / turbine prototype fields
		string electricityProduced = "";
		string consumedProduct = "";
		string producedProduct = "";
		string mechPowerOutput = "";
		string recipeDuration = "";

		if (proto != null)
		{
			protoId = GetPropertyString(proto, "Id");
			outputElectricity = GetPropertyString(proto, "OutputElectricity");
			duration = GetPropertyString(proto, "Duration");

			inputProduct = GetFieldString(proto, "InputProduct");
			outputProduct = GetFieldString(proto, "OutputProduct");

			// Solar / turbine prototype values
			electricityProduced = GetPropertyString(proto, "ElectricityProduced");
			if (string.IsNullOrWhiteSpace(electricityProduced))
			{
				electricityProduced = GetFieldString(proto, "OutputElectricity");
			}

			consumedProduct = GetFieldString(proto, "ConsumedProduct");
			producedProduct = GetFieldString(proto, "ProducedProduct");
			mechPowerOutput = GetFieldString(proto, "MechPowerOutput");
			recipeDuration = GetFieldString(proto, "RecipeDuration");
		}

		object inputBuffer = GetPropertyValue(entity, "InputBuffer");
		object outputBufferOption = GetPropertyValue(entity, "OutputBuffer");

		sb.AppendLine(pad + "{");
		sb.AppendLine(pad + "  \"entityId\": \"" + EscapeJson(entityId) + "\",");
		sb.AppendLine(pad + "  \"title\": \"" + EscapeJson(title) + "\",");
		sb.AppendLine(pad + "  \"customTitleRaw\": \"" + EscapeJson(customTitleRaw) + "\",");
		sb.AppendLine(pad + "  \"customTitle\": \"" + EscapeJson(customTitle) + "\",");
		sb.AppendLine(pad + "  \"displayTitle\": \"" + EscapeJson(displayTitle) + "\",");
		sb.AppendLine(pad + "  \"runtimeType\": \"" + EscapeJson(type.FullName) + "\",");
		sb.AppendLine(pad + "  \"shortType\": \"" + EscapeJson(type.Name) + "\",");
		sb.AppendLine(pad + "  \"prototypeId\": \"" + EscapeJson(protoId) + "\",");
		sb.AppendLine(pad + "  \"position\": \"" + EscapeJson(position) + "\",");
		sb.AppendLine(pad + "  \"currentState\": \"" + EscapeJson(currentState) + "\",");
		sb.AppendLine(pad + "  \"isEnabled\": " + isEnabled + ",");
		sb.AppendLine(pad + "  \"isPaused\": " + isPaused + ",");
		sb.AppendLine(pad + "  \"isEnabledNow\": " + isEnabledNow + ",");
		sb.AppendLine(pad + "  \"maxGenerationCapacity\": \"" + EscapeJson(maxGenerationCapacity) + "\",");
		sb.AppendLine(pad + "  \"generatedElectricityThisTick\": \"" + EscapeJson(generatedElectricityThisTick) + "\",");
		sb.AppendLine(pad + "  \"usedMechPowerThisTick\": \"" + EscapeJson(usedMechPowerThisTick) + "\",");
		sb.AppendLine(pad + "  \"maxOutputElectricity\": \"" + EscapeJson(maxOutputElectricity) + "\",");
		sb.AppendLine(pad + "  \"electricityProduced\": \"" + EscapeJson(electricityProduced) + "\",");
		sb.AppendLine(pad + "  \"powerGeneratedLastTick\": \"" + EscapeJson(powerGeneratedLastTick) + "\",");
		sb.AppendLine(pad + "  \"inputBufferQuantity\": \"" + EscapeJson(inputBufferQuantity) + "\",");
		sb.AppendLine(pad + "  \"outputBufferQuantity\": \"" + EscapeJson(outputBufferQuantity) + "\",");
		sb.AppendLine(pad + "  \"consumedProductRaw\": \"" + EscapeJson(consumedProduct) + "\",");
		sb.AppendLine(pad + "  \"producedProductRaw\": \"" + EscapeJson(producedProduct) + "\",");
		sb.AppendLine(pad + "  \"mechPowerOutput\": \"" + EscapeJson(mechPowerOutput) + "\",");
		sb.AppendLine(pad + "  \"recipeDuration\": \"" + EscapeJson(recipeDuration) + "\",");
		sb.AppendLine(pad + "  \"currentFuelUsage\": \"" + EscapeJson(currentFuelUsage) + "\",");
		sb.AppendLine(pad + "  \"currentProductLeft\": \"" + EscapeJson(currentProductLeft) + "\",");
		sb.AppendLine(pad + "  \"currentProgress\": \"" + EscapeJson(currentProgress) + "\",");
		sb.AppendLine(pad + "  \"outputElectricity\": \"" + EscapeJson(outputElectricity) + "\",");
		sb.AppendLine(pad + "  \"duration\": \"" + EscapeJson(duration) + "\",");
		sb.AppendLine(pad + "  \"inputProductRaw\": \"" + EscapeJson(inputProduct) + "\",");
		sb.AppendLine(pad + "  \"outputProductRaw\": \"" + EscapeJson(outputProduct) + "\",");

		sb.AppendLine(pad + "  \"inputBuffer\": ");
		WriteProductBufferJson(sb, inputBuffer, indent + 2);
		sb.AppendLine(",");

		sb.AppendLine(pad + "  \"outputBuffer\": ");
		WriteOptionProductBufferJson(sb, outputBufferOption, indent + 2);
		sb.AppendLine();

		sb.Append(pad + "}");
	}
	private static object GetPropertyValue(object target, string propertyName)
	{
		try
		{
			if (target == null)
			{
				return null;
			}

			var prop = target.GetType().GetProperty(
				propertyName,
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
			);

			if (prop == null || prop.GetIndexParameters().Length > 0)
			{
				return null;
			}

			return prop.GetValue(target, null);
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

			var field = target.GetType().GetField(
				fieldName,
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
			);

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
			object value = GetPrivateFieldValue(target, fieldName);
			return value == null ? "" : value.ToString();
		}
		catch
		{
			return "";
		}
	}
	private static void WriteProductBufferJson(StringBuilder sb, object buffer, int indent)
	{
		string pad = new string(' ', indent);

		if (buffer == null)
		{
			sb.AppendLine(pad + "null");
			return;
		}

		string product = GetPropertyString(buffer, "Product");
		string productId = ExtractProductId(product);

		sb.AppendLine(pad + "{");
		sb.AppendLine(pad + "  \"productId\": \"" + EscapeJson(productId) + "\",");
		sb.AppendLine(pad + "  \"productRaw\": \"" + EscapeJson(product) + "\",");
		sb.AppendLine(pad + "  \"quantity\": \"" + EscapeJson(GetPropertyString(buffer, "Quantity")) + "\",");
		sb.AppendLine(pad + "  \"capacity\": \"" + EscapeJson(GetPropertyString(buffer, "Capacity")) + "\",");
		sb.AppendLine(pad + "  \"usableCapacity\": \"" + EscapeJson(GetPropertyString(buffer, "UsableCapacity")) + "\",");
		sb.AppendLine(pad + "  \"isFull\": " + GetPropertyBoolJson(buffer, "IsFull") + ",");
		sb.AppendLine(pad + "  \"isEmpty\": " + GetPropertyBoolJson(buffer, "IsEmpty") + ",");
		sb.AppendLine(pad + "  \"isAnyPortConnected\": " + GetPropertyBoolJson(buffer, "IsAnyPortConnected") + ",");
		sb.AppendLine(pad + "  \"isRegisteredToLogistics\": " + GetPropertyBoolJson(buffer, "IsRegisteredToLogistics"));
		sb.Append(pad + "}");
	}

	private static void WriteOptionProductBufferJson(StringBuilder sb, object optionValue, int indent)
	{
		string pad = new string(' ', indent);

		if (optionValue == null)
		{
			sb.AppendLine(pad + "null");
			return;
		}

		string optionText = optionValue.ToString();

		if (string.IsNullOrWhiteSpace(optionText) || optionText.StartsWith("None:"))
		{
			sb.AppendLine(pad + "null");
			return;
		}

		object buffer = TryGetOptionValue(optionValue);

		if (buffer == null)
		{
			sb.AppendLine(pad + "{");
			sb.AppendLine(pad + "  \"optionRaw\": \"" + EscapeJson(optionText) + "\"");
			sb.Append(pad + "}");
			return;
		}

		WriteProductBufferJson(sb, buffer, indent);
	}

	private static object TryGetOptionValue(object optionValue)
	{
		try
		{
			if (optionValue == null)
			{
				return null;
			}

			var props = optionValue
				.GetType()
				.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
				.Where(p => p.GetIndexParameters().Length == 0)
				.ToList();

			foreach (string name in new[] { "Value", "ValueOrDefault", "Data" })
			{
				var prop = props.FirstOrDefault(p => p.Name == name);

				if (prop != null)
				{
					object value = prop.GetValue(optionValue, null);

					if (value != null)
					{
						return value;
					}
				}
			}

			var fields = optionValue
				.GetType()
				.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

			foreach (string name in new[] { "m_value", "value", "_value" })
			{
				var field = fields.FirstOrDefault(f => f.Name == name);

				if (field != null)
				{
					object value = field.GetValue(optionValue);

					if (value != null)
					{
						return value;
					}
				}
			}
		}
		catch
		{
			// Safe fallback.
		}

		return null;
	}

	private static string ExtractProductId(string productRaw)
	{
		if (string.IsNullOrWhiteSpace(productRaw))
		{
			return "";
		}

		string trimmed = productRaw.Trim();

		int firstSpace = trimmed.IndexOf(" ");

		if (firstSpace > 0)
		{
			return trimmed.Substring(0, firstSpace);
		}

		return trimmed;
	}

	private static string BuildPowerAssetProbeJson(IEntitiesManager entitiesManager)
	{
		var typeNames = new[] {
		"Mafi.Base.Prototypes.Machines.SolarElectricityGenerator",
		"Mafi.Base.Prototypes.Machines.PowerGenerators.MechPowerGeneratorFromProduct",
		"Mafi.Base.Prototypes.Machines.PowerGenerators.ElectricityGeneratorFromMechPower"
	};

		var sb = new StringBuilder();

		sb.AppendLine("{");
		sb.AppendLine("  \"exportType\": \"powerAssetProbe\",");
		sb.AppendLine("  \"purpose\": \"Focused probe for solar generation, turbine steam/input/output data, and mechanical power chain data.\",");
		sb.AppendLine("  \"types\": [");

		for (int i = 0; i < typeNames.Length; i++)
		{
			WriteFocusedPowerAssetTypeProbe(sb, entitiesManager, typeNames[i], 4);

			if (i < typeNames.Length - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.AppendLine("  ]");
		sb.AppendLine("}");

		return sb.ToString();
	}
	private static void WriteFocusedPowerAssetTypeProbe(
		StringBuilder sb,
		IEntitiesManager entitiesManager,
		string typeName,
		int indent
	)
	{
		string pad = new string(' ', indent);

		sb.AppendLine(pad + "{");
		sb.AppendLine(pad + "  \"typeName\": \"" + EscapeJson(typeName) + "\",");

		try
		{
			Type type = FindTypeByFullName(typeName);

			if (type == null)
			{
				sb.AppendLine(pad + "  \"typeFound\": false");
				sb.Append(pad + "}");
				return;
			}

			var entities = GetAllEntitiesOfTypeReflective(entitiesManager, type);

			sb.AppendLine(pad + "  \"typeFound\": true,");
			sb.AppendLine(pad + "  \"assembly\": \"" + EscapeJson(type.Assembly.GetName().Name) + "\",");
			sb.AppendLine(pad + "  \"entityCount\": " + entities.Count + ",");

			WriteFocusedMemberNameScan(sb, type, "interestingMembers", indent + 2);
			sb.AppendLine(",");

			sb.AppendLine(pad + "  \"samples\": [");

			int max = Math.Min(entities.Count, 10);

			for (int i = 0; i < max; i++)
			{
				WriteFocusedPowerAssetObjectProbe(sb, entities[i], indent + 4);

				if (i < max - 1)
				{
					sb.Append(",");
				}

				sb.AppendLine();
			}

			sb.AppendLine(pad + "  ]");
			sb.Append(pad + "}");
		}
		catch (Exception ex)
		{
			sb.AppendLine(pad + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
			sb.Append(pad + "}");
		}
	}
	private static void WriteFocusedPowerAssetObjectProbe(StringBuilder sb, object entity, int indent)
	{
		string pad = new string(' ', indent);

		sb.AppendLine(pad + "{");

		if (entity == null)
		{
			sb.AppendLine(pad + "  \"isNull\": true");
			sb.Append(pad + "}");
			return;
		}

		try
		{
			Type type = entity.GetType();

			sb.AppendLine(pad + "  \"runtimeType\": \"" + EscapeJson(type.FullName) + "\",");
			sb.AppendLine(pad + "  \"toString\": \"" + EscapeJson(entity.ToString()) + "\",");

			string[] directProperties = new[] {
			"Id",
			"DefaultTitle",
			"CurrentState",
			"State",
			"IsEnabled",
			"IsPaused",
			"IsEnabledNow",
			"Position3f",

			// Solar / electric generation suspects
			"MaxGenerationCapacity",
			"GenerationCapacity",
			"CurrentGeneration",
			"CurrentGeneratedElectricity",
			"GeneratedElectricityThisTick",
			"GeneratedPowerThisTick",
			"OutputElectricity",
			"CurrentOutputElectricity",

			// Mechanical power suspects
			"GeneratedMechPowerThisTick",
			"ProducedMechPowerThisTick",
			"CurrentMechPower",
			"UsedMechPowerThisTick",
			"MaxMechPower",

			// Product / recipe / machine suspects
			"InputBuffer",
			"OutputBuffer",
			"InputBuffers",
			"OutputBuffers",
			"CurrentRecipe",
			"Recipe",
			"SelectedRecipe",
			"Prototype",
			"Proto",

			// Component suspects
			"ElectricityGenerator",
			"ElectricityConsumer",
			"MechPowerGenerator",
			"MechanicalPowerGenerator",
			"ProductConsumer",
			"ProductProducer"
		};

			sb.AppendLine(pad + "  \"directProperties\": {");

			for (int i = 0; i < directProperties.Length; i++)
			{
				WriteFocusedPropertyProbe(sb, entity, directProperties[i], indent + 4);

				if (i < directProperties.Length - 1)
				{
					sb.Append(",");
				}

				sb.AppendLine();
			}

			sb.AppendLine(pad + "  },");

			string[] privateFields = new[] {
			"m_proto",
			"m_state",
			"m_currentState",

			"m_electricityGenerator",
			"m_electricityConsumer",
			"m_generator",
			"m_consumer",

			"m_mechPowerGenerator",
			"m_mechanicalPowerGenerator",
			"m_powerGenerator",
			"m_powerConsumer",

			"m_productConsumer",
			"m_productProducer",

			"m_inputBuffer",
			"m_outputBuffer",
			"m_inputBuffers",
			"m_outputBuffers",
			"m_productBuffer",

			"m_currentRecipe",
			"m_recipe",
			"m_selectedRecipe",

			"m_maxGenerationCapacity",
			"m_generatedElectricityThisTick",
			"m_generatedMechPowerThisTick",
			"m_producedMechPowerThisTick",
			"m_usedMechPowerThisTick"
		};

			sb.AppendLine(pad + "  \"privateFields\": {");

			for (int i = 0; i < privateFields.Length; i++)
			{
				WriteFocusedFieldProbe(sb, entity, privateFields[i], indent + 4);

				if (i < privateFields.Length - 1)
				{
					sb.Append(",");
				}

				sb.AppendLine();
			}

			sb.AppendLine(pad + "  },");

			object proto =
				GetPrivateFieldValue(entity, "m_proto") ??
				GetPropertyValue(entity, "Prototype") ??
				GetPropertyValue(entity, "Proto");

			sb.AppendLine(pad + "  \"prototypeProbe\": ");
			WriteFocusedNestedObjectProbe(sb, proto, indent + 2);
			sb.AppendLine(",");

			object electricityGenerator =
				GetPropertyValue(entity, "ElectricityGenerator") ??
				GetPrivateFieldValue(entity, "m_electricityGenerator");

			sb.AppendLine(pad + "  \"electricityGeneratorProbe\": ");
			WriteFocusedNestedObjectProbe(sb, electricityGenerator, indent + 2);
			sb.AppendLine(",");

			object mechPowerGenerator =
				GetPropertyValue(entity, "MechPowerGenerator") ??
				GetPropertyValue(entity, "MechanicalPowerGenerator") ??
				GetPrivateFieldValue(entity, "m_mechPowerGenerator") ??
				GetPrivateFieldValue(entity, "m_mechanicalPowerGenerator") ??
				GetPrivateFieldValue(entity, "m_powerGenerator");

			sb.AppendLine(pad + "  \"mechanicalGeneratorProbe\": ");
			WriteFocusedNestedObjectProbe(sb, mechPowerGenerator, indent + 2);
			sb.AppendLine(",");

			object inputBuffer =
				GetPropertyValue(entity, "InputBuffer") ??
				GetPrivateFieldValue(entity, "m_inputBuffer") ??
				GetPrivateFieldValue(entity, "m_inputBuffers");

			sb.AppendLine(pad + "  \"inputBufferProbe\": ");
			WriteFocusedNestedObjectProbe(sb, inputBuffer, indent + 2);
			sb.AppendLine(",");

			object outputBuffer =
				GetPropertyValue(entity, "OutputBuffer") ??
				GetPrivateFieldValue(entity, "m_outputBuffer") ??
				GetPrivateFieldValue(entity, "m_outputBuffers");

			sb.AppendLine(pad + "  \"outputBufferProbe\": ");
			WriteFocusedNestedObjectProbe(sb, outputBuffer, indent + 2);
			sb.AppendLine();

			sb.Append(pad + "}");
		}
		catch (Exception ex)
		{
			sb.AppendLine(pad + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
			sb.Append(pad + "}");
		}
	}
	private static void WriteFocusedPropertyProbe(
		StringBuilder sb,
		object target,
		string propertyName,
		int indent
	)
	{
		string pad = new string(' ', indent);

		try
		{
			if (target == null)
			{
				sb.Append(pad + "\"" + EscapeJson(propertyName) + "\": { \"found\": false, \"isNullTarget\": true }");
				return;
			}

			var prop = target.GetType().GetProperty(
				propertyName,
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
			);

			if (prop == null || prop.GetIndexParameters().Length > 0)
			{
				sb.Append(pad + "\"" + EscapeJson(propertyName) + "\": { \"found\": false }");
				return;
			}

			object value = prop.GetValue(target, null);

			sb.Append(pad + "\"" + EscapeJson(propertyName) + "\": { ");
			sb.Append("\"found\": true, ");
			sb.Append("\"declaredType\": \"" + EscapeJson(prop.PropertyType.FullName) + "\", ");
			sb.Append("\"isNull\": " + (value == null ? "true" : "false") + ", ");
			sb.Append("\"runtimeType\": \"" + EscapeJson(value == null ? "" : value.GetType().FullName) + "\", ");
			sb.Append("\"value\": \"" + EscapeJson(value == null ? "" : value.ToString()) + "\"");
			sb.Append(" }");
		}
		catch (Exception ex)
		{
			sb.Append(pad + "\"" + EscapeJson(propertyName) + "\": { ");
			sb.Append("\"found\": false, ");
			sb.Append("\"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
			sb.Append(" }");
		}
	}

	private static void WriteFocusedFieldProbe(
		StringBuilder sb,
		object target,
		string fieldName,
		int indent
	)
	{
		string pad = new string(' ', indent);

		try
		{
			if (target == null)
			{
				sb.Append(pad + "\"" + EscapeJson(fieldName) + "\": { \"found\": false, \"isNullTarget\": true }");
				return;
			}

			var field = target.GetType().GetField(
				fieldName,
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
			);

			if (field == null)
			{
				sb.Append(pad + "\"" + EscapeJson(fieldName) + "\": { \"found\": false }");
				return;
			}

			object value = field.GetValue(target);

			sb.Append(pad + "\"" + EscapeJson(fieldName) + "\": { ");
			sb.Append("\"found\": true, ");
			sb.Append("\"declaredType\": \"" + EscapeJson(field.FieldType.FullName) + "\", ");
			sb.Append("\"isNull\": " + (value == null ? "true" : "false") + ", ");
			sb.Append("\"runtimeType\": \"" + EscapeJson(value == null ? "" : value.GetType().FullName) + "\", ");
			sb.Append("\"value\": \"" + EscapeJson(value == null ? "" : value.ToString()) + "\"");
			sb.Append(" }");
		}
		catch (Exception ex)
		{
			sb.Append(pad + "\"" + EscapeJson(fieldName) + "\": { ");
			sb.Append("\"found\": false, ");
			sb.Append("\"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
			sb.Append(" }");
		}
	}

	private static void WriteFocusedNestedObjectProbe(StringBuilder sb, object value, int indent)
	{
		string pad = new string(' ', indent);

		if (value == null)
		{
			sb.AppendLine(pad + "null");
			return;
		}

		try
		{
			Type type = value.GetType();

			sb.AppendLine(pad + "{");
			sb.AppendLine(pad + "  \"runtimeType\": \"" + EscapeJson(type.FullName) + "\",");
			sb.AppendLine(pad + "  \"toString\": \"" + EscapeJson(value.ToString()) + "\",");

			WriteFocusedMemberNameScan(sb, type, "interestingMembers", indent + 2);
			sb.AppendLine(",");

			string[] nestedProperties = new[] {
			"Id",
			"Product",
			"Quantity",
			"Capacity",
			"UsableCapacity",
			"IsFull",
			"IsEmpty",
			"InputProduct",
			"OutputProduct",
			"InputProducts",
			"OutputProducts",
			"Inputs",
			"Outputs",
			"Duration",
			"OutputElectricity",
			"MaxGenerationCapacity",
			"GeneratedElectricityThisTick",
			"GeneratedMechPowerThisTick",
			"ProducedMechPowerThisTick",
			"UsedMechPowerThisTick",
			"CurrentState",
			"State"
		};

			sb.AppendLine(pad + "  \"candidateProperties\": {");

			for (int i = 0; i < nestedProperties.Length; i++)
			{
				WriteFocusedPropertyProbe(sb, value, nestedProperties[i], indent + 4);

				if (i < nestedProperties.Length - 1)
				{
					sb.Append(",");
				}

				sb.AppendLine();
			}

			sb.AppendLine(pad + "  }");
			sb.Append(pad + "}");
		}
		catch (Exception ex)
		{
			sb.AppendLine(pad + "{");
			sb.AppendLine(pad + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
			sb.Append(pad + "}");
		}
	}

	private static void WriteFocusedMemberNameScan(
		StringBuilder sb,
		Type type,
		string propertyName,
		int indent
	)
	{
		string pad = new string(' ', indent);

		string[] interestingTerms = new[] {
		"power",
		"electric",
		"mech",
		"steam",
		"input",
		"output",
		"buffer",
		"product",
		"recipe",
		"capacity",
		"generation",
		"generated",
		"state",
		"proto",
		"duration",
		"fuel",
		"consumer",
		"producer"
	};

		bool IsInteresting(string name)
		{
			string lower = name.ToLowerInvariant();
			return interestingTerms.Any(term => lower.Contains(term));
		}

		var props = type
			.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
			.Where(p => p.GetIndexParameters().Length == 0 && IsInteresting(p.Name))
			.Select(p => new
			{
				kind = "property",
				name = p.Name,
				typeName = p.PropertyType.FullName
			})
			.OrderBy(p => p.name)
			.Take(120)
			.ToList();

		var fields = type
			.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
			.Where(f => IsInteresting(f.Name))
			.Select(f => new
			{
				kind = "field",
				name = f.Name,
				typeName = f.FieldType.FullName
			})
			.OrderBy(f => f.name)
			.Take(120)
			.ToList();

		sb.AppendLine(pad + "\"" + EscapeJson(propertyName) + "\": [");

		int total = props.Count + fields.Count;
		int index = 0;

		foreach (var prop in props)
		{
			sb.Append(pad + "  { ");
			sb.Append("\"kind\": \"" + EscapeJson(prop.kind) + "\", ");
			sb.Append("\"name\": \"" + EscapeJson(prop.name) + "\", ");
			sb.Append("\"type\": \"" + EscapeJson(prop.typeName) + "\"");
			sb.Append(" }");

			index++;

			if (index < total)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		foreach (var field in fields)
		{
			sb.Append(pad + "  { ");
			sb.Append("\"kind\": \"" + EscapeJson(field.kind) + "\", ");
			sb.Append("\"name\": \"" + EscapeJson(field.name) + "\", ");
			sb.Append("\"type\": \"" + EscapeJson(field.typeName) + "\"");
			sb.Append(" }");

			index++;

			if (index < total)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.Append(pad + "]");
	}

	private static string BuildEntityNameProbeJson(IEntitiesManager entitiesManager)
	{
		var typeNames = new[] {
		"Mafi.Base.Prototypes.Machines.PowerGenerators.ElectricityGeneratorFromProduct",
		"Mafi.Base.Prototypes.Machines.PowerGenerators.ElectricityGeneratorFromMechPower",
		"Mafi.Base.Prototypes.Machines.PowerGenerators.MechPowerGeneratorFromProduct",
		"Mafi.Base.Prototypes.Machines.SolarElectricityGenerator"
	};

		var sb = new StringBuilder();

		sb.AppendLine("{");
		sb.AppendLine("  \"exportType\": \"entityNameProbe\",");
		sb.AppendLine("  \"purpose\": \"Focused probe to discover player-renamed/custom machine names versus default prototype titles.\",");
		sb.AppendLine("  \"searchHint\": \"Rename machines in-game to obvious names like CALDER TEST TURBINE, then search this file for CALDER.\",");
		sb.AppendLine("  \"types\": [");

		for (int i = 0; i < typeNames.Length; i++)
		{
			WriteEntityNameTypeProbe(sb, entitiesManager, typeNames[i], 4);

			if (i < typeNames.Length - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.AppendLine("  ]");
		sb.AppendLine("}");

		return sb.ToString();
	}

	private static void WriteEntityNameTypeProbe(
		StringBuilder sb,
		IEntitiesManager entitiesManager,
		string typeName,
		int indent
	)
	{
		string pad = new string(' ', indent);

		sb.AppendLine(pad + "{");
		sb.AppendLine(pad + "  \"typeName\": \"" + EscapeJson(typeName) + "\",");

		try
		{
			Type type = FindTypeByFullName(typeName);

			if (type == null)
			{
				sb.AppendLine(pad + "  \"typeFound\": false");
				sb.Append(pad + "}");
				return;
			}

			var entities = GetAllEntitiesOfTypeReflective(entitiesManager, type);

			sb.AppendLine(pad + "  \"typeFound\": true,");
			sb.AppendLine(pad + "  \"assembly\": \"" + EscapeJson(type.Assembly.GetName().Name) + "\",");
			sb.AppendLine(pad + "  \"entityCount\": " + entities.Count + ",");

			WriteNameMemberSummary(sb, type, "nameLikeMembersOnType", indent + 2);
			sb.AppendLine(",");

			sb.AppendLine(pad + "  \"samples\": [");

			int max = Math.Min(entities.Count, 100);

			for (int i = 0; i < max; i++)
			{
				WriteEntityNameObjectProbe(sb, entities[i], indent + 4);

				if (i < max - 1)
				{
					sb.Append(",");
				}

				sb.AppendLine();
			}

			sb.AppendLine(pad + "  ]");
			sb.Append(pad + "}");
		}
		catch (Exception ex)
		{
			sb.AppendLine(pad + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
			sb.Append(pad + "}");
		}
	}

	private static void WriteEntityNameObjectProbe(StringBuilder sb, object entity, int indent)
	{
		string pad = new string(' ', indent);

		sb.AppendLine(pad + "{");

		if (entity == null)
		{
			sb.AppendLine(pad + "  \"isNull\": true");
			sb.Append(pad + "}");
			return;
		}

		try
		{
			Type type = entity.GetType();

			sb.AppendLine(pad + "  \"runtimeType\": \"" + EscapeJson(type.FullName) + "\",");
			sb.AppendLine(pad + "  \"toString\": \"" + EscapeJson(entity.ToString()) + "\",");

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

			sb.AppendLine(pad + "  \"nameLikeMemberValues\": [");
			WriteNameLikeMemberValues(sb, entity, indent + 4);
			sb.AppendLine();
			sb.AppendLine(pad + "  ],");

			object proto =
				GetPrivateFieldValue(entity, "m_proto") ??
				GetPropertyValue(entity, "Prototype") ??
				GetPropertyValue(entity, "Proto");

			sb.AppendLine(pad + "  \"prototype\": ");
			WriteNameNestedProbe(sb, proto, indent + 2);
			sb.AppendLine();

			sb.Append(pad + "}");
		}
		catch (Exception ex)
		{
			sb.AppendLine(pad + "  \"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"");
			sb.Append(pad + "}");
		}
	}

	private static void WriteNameCandidateValue(
		StringBuilder sb,
		object target,
		string memberName,
		int indent
	)
	{
		string pad = new string(' ', indent);

		sb.Append(pad + "\"" + EscapeJson(memberName) + "\": ");

		try
		{
			object value = GetMemberValueByExactName(target, memberName);

			if (value == null)
			{
				sb.Append("{ \"found\": false }");
				return;
			}

			sb.Append("{ ");
			sb.Append("\"found\": true, ");
			sb.Append("\"runtimeType\": \"" + EscapeJson(value.GetType().FullName) + "\", ");
			sb.Append("\"value\": \"" + EscapeJson(value.ToString()) + "\"");
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

		var properties = type.GetProperties(
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
		);

		foreach (var prop in properties)
		{
			if (prop.Name != memberName || prop.GetIndexParameters().Length > 0)
			{
				continue;
			}

			try
			{
				return prop.GetValue(target, null);
			}
			catch
			{
				// Try next matching member if inheritance produced awkward duplicates.
			}
		}

		var fields = type.GetFields(
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
		);

		foreach (var field in fields)
		{
			if (field.Name != memberName)
			{
				continue;
			}

			try
			{
				return field.GetValue(target);
			}
			catch
			{
				// Try next matching member.
			}
		}

		return null;
	}

	private static void WriteNameLikeMemberValues(StringBuilder sb, object target, int indent)
	{
		string pad = new string(' ', indent);

		if (target == null)
		{
			return;
		}

		Type type = target.GetType();

		string[] terms = new[] {
		"name",
		"title",
		"display",
		"label",
		"caption",
		"custom",
		"user"
	};

		bool IsInteresting(string name)
		{
			string lower = name.ToLowerInvariant();
			return terms.Any(term => lower.Contains(term));
		}

		var entries = new System.Collections.Generic.List<string>();

		foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
		{
			if (prop.GetIndexParameters().Length > 0 || !IsInteresting(prop.Name))
			{
				continue;
			}

			try
			{
				object value = prop.GetValue(target, null);

				entries.Add(
					"{ \"kind\": \"property\", " +
					"\"name\": \"" + EscapeJson(prop.Name) + "\", " +
					"\"declaredType\": \"" + EscapeJson(prop.PropertyType.FullName) + "\", " +
					"\"valueType\": \"" + EscapeJson(value == null ? "" : value.GetType().FullName) + "\", " +
					"\"value\": \"" + EscapeJson(value == null ? "" : value.ToString()) + "\" }"
				);
			}
			catch (Exception ex)
			{
				entries.Add(
					"{ \"kind\": \"property\", " +
					"\"name\": \"" + EscapeJson(prop.Name) + "\", " +
					"\"declaredType\": \"" + EscapeJson(prop.PropertyType.FullName) + "\", " +
					"\"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\" }"
				);
			}
		}

		foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
		{
			if (!IsInteresting(field.Name))
			{
				continue;
			}

			try
			{
				object value = field.GetValue(target);

				entries.Add(
					"{ \"kind\": \"field\", " +
					"\"name\": \"" + EscapeJson(field.Name) + "\", " +
					"\"declaredType\": \"" + EscapeJson(field.FieldType.FullName) + "\", " +
					"\"valueType\": \"" + EscapeJson(value == null ? "" : value.GetType().FullName) + "\", " +
					"\"value\": \"" + EscapeJson(value == null ? "" : value.ToString()) + "\" }"
				);
			}
			catch (Exception ex)
			{
				entries.Add(
					"{ \"kind\": \"field\", " +
					"\"name\": \"" + EscapeJson(field.Name) + "\", " +
					"\"declaredType\": \"" + EscapeJson(field.FieldType.FullName) + "\", " +
					"\"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\" }"
				);
			}
		}

		for (int i = 0; i < entries.Count; i++)
		{
			sb.Append(pad + entries[i]);

			if (i < entries.Count - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}
	}

	private static void WriteNameMemberSummary(
		StringBuilder sb,
		Type type,
		string propertyName,
		int indent
	)
	{
		string pad = new string(' ', indent);

		string[] terms = new[] {
		"name",
		"title",
		"display",
		"label",
		"caption",
		"custom",
		"user"
	};

		bool IsInteresting(string name)
		{
			string lower = name.ToLowerInvariant();
			return terms.Any(term => lower.Contains(term));
		}

		var members = new System.Collections.Generic.List<string>();

		foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
		{
			if (prop.GetIndexParameters().Length > 0 || !IsInteresting(prop.Name))
			{
				continue;
			}

			members.Add(
				"{ \"kind\": \"property\", " +
				"\"name\": \"" + EscapeJson(prop.Name) + "\", " +
				"\"type\": \"" + EscapeJson(prop.PropertyType.FullName) + "\" }"
			);
		}

		foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
		{
			if (!IsInteresting(field.Name))
			{
				continue;
			}

			members.Add(
				"{ \"kind\": \"field\", " +
				"\"name\": \"" + EscapeJson(field.Name) + "\", " +
				"\"type\": \"" + EscapeJson(field.FieldType.FullName) + "\" }"
			);
		}

		sb.AppendLine(pad + "\"" + EscapeJson(propertyName) + "\": [");

		for (int i = 0; i < members.Count; i++)
		{
			sb.Append(pad + "  " + members[i]);

			if (i < members.Count - 1)
			{
				sb.Append(",");
			}

			sb.AppendLine();
		}

		sb.Append(pad + "]");
	}

	private static void WriteNameNestedProbe(StringBuilder sb, object value, int indent)
	{
		string pad = new string(' ', indent);

		if (value == null)
		{
			sb.AppendLine(pad + "null");
			return;
		}

		sb.AppendLine(pad + "{");
		sb.AppendLine(pad + "  \"runtimeType\": \"" + EscapeJson(value.GetType().FullName) + "\",");
		sb.AppendLine(pad + "  \"toString\": \"" + EscapeJson(value.ToString()) + "\",");

		WriteNameMemberSummary(sb, value.GetType(), "nameLikeMembers", indent + 2);
		sb.AppendLine(",");

		sb.AppendLine(pad + "  \"nameLikeMemberValues\": [");
		WriteNameLikeMemberValues(sb, value, indent + 4);
		sb.AppendLine();
		sb.AppendLine(pad + "  ]");

		sb.Append(pad + "}");
	}

	private static string CleanOptionString(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "";
		}

		string cleaned = value.Trim();

		if (cleaned.StartsWith("Some: "))
		{
			return cleaned.Substring("Some: ".Length).Trim();
		}

		if (cleaned.StartsWith("None:"))
		{
			return "";
		}

		return cleaned;
	}
}