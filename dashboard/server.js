const http = require("http");
const fs = require("fs");
const path = require("path");

const PORT = 3030;

const exportFolder = path.join(
  process.env.APPDATA,
  "Captain of Industry",
  "Mods",
  "CLExporter",
);

const TICKS_PER_MINUTE = 600;

const recipesPath = path.join(exportFolder, "coi_recipes.json");
const productsPath = path.join(exportFolder, "coi_products.json");
const worldMachinesPath = path.join(exportFolder, "coi_world_machines.json");
const powerPath = path.join(exportFolder, "coi_power.json");
const powerEntitiesPath = path.join(exportFolder, "coi_power_entities.json");

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf-8"));
}

function parseQuantity(value) {
  const number = Number(
    String(value ?? "")
      .replace(",", "")
      .trim(),
  );
  return Number.isNaN(number) ? 0 : number;
}

function parseDurationTicks(value) {
  const number = Number(
    String(value ?? "")
      .replace(",", "")
      .trim(),
  );
  return Number.isNaN(number) || number <= 0 ? 0 : number;
}

function parsePercent(value) {
  const number = Number(
    String(value ?? "")
      .replace("%", "")
      .trim(),
  );
  return Number.isNaN(number) ? 0 : number / 100;
}

function cleanRecipeId(value) {
  if (!value) return "";

  let cleaned = String(value).trim();

  if (cleaned.startsWith("Some: ")) {
    cleaned = cleaned.substring("Some: ".length).trim();
  }

  if (cleaned.startsWith("None: ")) {
    return "";
  }

  if (cleaned.endsWith(" (recipe)")) {
    cleaned = cleaned.substring(0, cleaned.length - " (recipe)".length).trim();
  }

  return cleaned;
}

function getSelectedRecipeId(machine) {
  const lastRecipe = cleanRecipeId(machine.lastRecipeInProgressRaw);

  if (lastRecipe) {
    return lastRecipe;
  }

  if (machine.assignedRecipes && machine.assignedRecipes.length > 0) {
    return cleanRecipeId(machine.assignedRecipes[0]);
  }

  return "";
}

function normaliseMachineIdentity(machine) {
  const title = machine.title || "Machine";
  const customTitle = machine.customTitle || "";
  const displayTitle = machine.displayTitle || customTitle || title;

  return {
    ...machine,
    title,
    customTitle,
    displayTitle,
  };
}

function parsePowerNumber(value) {
  const number = Number(
    String(value ?? "")
      .replace(",", "")
      .trim(),
  );
  return Number.isNaN(number) ? 0 : number;
}

function calculatePowerSummary() {
  try {
    if (!fs.existsSync(powerPath)) {
      return {
        success: false,
        error: "coi_power.json not found",
      };
    }

    const powerExport = readJson(powerPath);

    if (!powerExport.success) {
      return {
        success: false,
        error: powerExport.error || "Power export unsuccessful",
      };
    }

    const demanded = parsePowerNumber(powerExport.demandedThisTick);
    const consumed = parsePowerNumber(powerExport.consumedThisTick);
    const generated = parsePowerNumber(powerExport.generatedThisTick);
    const generationCapacity = parsePowerNumber(
      powerExport.generationCapacityThisTick,
    );
    const maxGenerationCapacity = parsePowerNumber(
      powerExport.maxGenerationCapacity,
    );
    const wasted = parsePowerNumber(powerExport.wastedElectricityThisTick);
    const generatorsCount = parsePowerNumber(powerExport.generatorsCount);
    const consumersCount = parsePowerNumber(powerExport.consumersCount);

    const capacityMargin = generationCapacity - demanded;
    const currentMargin = generated - consumed;
    const capacityUtilizationPercent =
      generationCapacity > 0 ? (demanded / generationCapacity) * 100 : 0;

    let status = "POWER OK";

    if (capacityMargin < 0) {
      status = "POWER SHORTFALL";
    } else if (capacityUtilizationPercent >= 90) {
      status = "POWER NEAR LIMIT";
    }

    return {
      success: true,
      demanded,
      consumed,
      generated,
      generationCapacity,
      maxGenerationCapacity,
      wasted,
      generatorsCount,
      consumersCount,
      capacityMargin,
      currentMargin,
      capacityUtilizationPercent,
      status,
    };
  } catch (error) {
    return {
      success: false,
      error: error.name + ": " + error.message,
    };
  }
}

function parseSimplePercent(value) {
  const number = Number(
    String(value ?? "")
      .replace("%", "")
      .trim(),
  );
  return Number.isNaN(number) ? 0 : number;
}

function productIdFromRawProductQuantity(value) {
  const text = String(value ?? "");

  const match = text.match(/Product_[A-Za-z0-9_]+/);

  return match ? match[0] : "";
}

function quantityFromRawProductQuantity(value) {
  const text = String(value ?? "");
  const match = text.match(/^\s*([0-9.]+)/);

  return match ? parseQuantity(match[1]) : 0;
}

function formatProductLabel(value) {
  const raw = String(value ?? "").trim();
  const productMatch = raw.match(/Product_[A-Za-z0-9_]+/);
  let text = productMatch ? productMatch[0] : raw;

  text = text
    .replace(/^Product_/, "")
    .replace(/_/g, " ")
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .replace(/\s+/g, " ")
    .trim();

  return text ? text.charAt(0).toUpperCase() + text.slice(1) : "-";
}

function cleanPowerGeneratorType(shortType, title) {
  const lowerTitle = String(title ?? "").toLowerCase();

  if (shortType === "ElectricityGeneratorFromProduct") {
    return "Fuel generator";
  }

  if (shortType === "ElectricityGeneratorFromMechPower") {
    return "Electric generator";
  }

  if (shortType === "MechPowerGeneratorFromProduct") {
    if (lowerTitle.includes("turbine")) {
      return "Turbine";
    }

    return "Mechanical generator";
  }

  if (shortType === "FlyWheelEntity") {
    return "Flywheel";
  }

  if (shortType === "SolarElectricityGenerator") {
    return "Solar";
  }

  return shortType || "Generator";
}

function isDieselGenerator(generator) {
  return (
    String(generator.title || "")
      .toLowerCase()
      .includes("diesel") ||
    String(generator.inputProductId || "") === "Product_Diesel" ||
    String(generator.inputProductRaw || "").includes("Product_Diesel")
  );
}

function isMechanicalSource(generator) {
  return generator.shortType === "MechPowerGeneratorFromProduct";
}

function isMechanicalConsumer(generator) {
  return generator.shortType === "ElectricityGeneratorFromMechPower";
}

function isElectricalGenerator(generator) {
  return (
    generator.shortType === "ElectricityGeneratorFromProduct" ||
    generator.shortType === "ElectricityGeneratorFromMechPower" ||
    generator.shortType === "SolarElectricityGenerator"
  );
}

function generatorStatus(generator) {
  if (generator.isEnabled !== true || generator.isEnabledNow !== true) {
    return "DISABLED";
  }

  if (generator.isPaused === true) {
    return "PAUSED";
  }

  const inputBuffer = generator.inputBuffer;

  if (inputBuffer && inputBuffer.isEmpty === true) {
    return "FUEL EMPTY";
  }

  if (inputBuffer) {
    const quantity = parseQuantity(inputBuffer.quantity);
    const capacity = parseQuantity(inputBuffer.capacity);

    if (capacity > 0 && quantity / capacity <= 0.15) {
      return "LOW FUEL";
    }
  }

  const maxCapacity =
    parsePowerNumber(generator.maxGenerationCapacity) ||
    parsePowerNumber(generator.maxOutputElectricity) ||
    parsePowerNumber(generator.electricityProduced);

  const generated =
    parsePowerNumber(generator.generatedElectricityThisTick) ||
    parsePowerNumber(generator.powerGeneratedLastTick);

  const currentState = String(generator.currentState ?? "").toLowerCase();
  if (maxCapacity > 0 && generated > 0 && generated < maxCapacity * 0.25) {
    return "LOW OUTPUT";
  }

  if (currentState && currentState !== "working" && currentState !== "none") {
    return generator.currentState.toUpperCase();
  }

  if (currentState === "none" && maxCapacity > 0 && generated <= 0) {
    return "IDLE";
  }

  return "OK";
}

function calculatePowerEntitiesSummary() {
  try {
    if (!fs.existsSync(powerEntitiesPath)) {
      return {
        success: false,
        error: "coi_power_entities.json not found",
        generators: [],
      };
    }

    const exportData = readJson(powerEntitiesPath);

    if (!exportData.success) {
      return {
        success: false,
        error: exportData.error || "Power entities export unsuccessful",
        generators: [],
      };
    }

    const generators = (exportData.generators ?? []).map((generator) => {
      const electricalCapacity =
        parsePowerNumber(generator.maxGenerationCapacity) ||
        parsePowerNumber(generator.maxOutputElectricity) ||
        parsePowerNumber(generator.electricityProduced) ||
        parsePowerNumber(generator.outputElectricity);

      const electricalCurrent = parsePowerNumber(
        generator.generatedElectricityThisTick,
      );

      const mechanicalCapacity = parsePowerNumber(generator.mechPowerOutput);
      const mechanicalCurrent = parsePowerNumber(
        generator.powerGeneratedLastTick,
      );
      const mechanicalUse = parsePowerNumber(generator.usedMechPowerThisTick);

      const outputElectricity = parsePowerNumber(generator.outputElectricity);

      const inputBuffer = generator.inputBuffer ?? null;

      const inputProductId =
        inputBuffer?.productId ||
        productIdFromRawProductQuantity(generator.inputProductRaw) ||
        productIdFromRawProductQuantity(generator.consumedProductRaw);

      const outputProductId =
        generator.outputBuffer?.productId ||
        productIdFromRawProductQuantity(generator.outputProductRaw) ||
        productIdFromRawProductQuantity(generator.producedProductRaw);

      const inputQuantity = inputBuffer
        ? parseQuantity(inputBuffer.quantity)
        : parseQuantity(generator.inputBufferQuantity);

      const inputCapacity = inputBuffer
        ? parseQuantity(inputBuffer.capacity)
        : quantityFromRawProductQuantity(generator.consumedProductRaw);

      const outputQuantity = parseQuantity(generator.outputBufferQuantity);
      const outputCapacity = quantityFromRawProductQuantity(
        generator.producedProductRaw,
      );

      const fuelPercent =
        inputCapacity > 0 ? (inputQuantity / inputCapacity) * 100 : null;

      const outputPercent =
        outputCapacity > 0 ? (outputQuantity / outputCapacity) * 100 : null;

      const typeLabel = cleanPowerGeneratorType(
        generator.shortType,
        generator.title,
      );
      const status = generatorStatus(generator);
      const enrichedGenerator = {
        ...generator,
        typeLabel,
        status,
        maxCapacity: electricalCapacity,
        generated: electricalCurrent,
        mechanicalCapacity,
        mechanicalCurrent,
        mechanicalUse,
        outputElectricity,

        inputProductId,
        inputProductName: formatProductLabel(
          inputProductId ||
            generator.inputProductRaw ||
            generator.consumedProductRaw,
        ),

        outputProductId,
        outputProductName: formatProductLabel(
          outputProductId ||
            generator.outputProductRaw ||
            generator.producedProductRaw,
        ),

        inputQuantity,
        inputCapacity,
        fuelPercent,
        outputQuantity,
        outputCapacity,
        outputPercent,
        currentFuelUsagePercent: parseSimplePercent(generator.currentFuelUsage),
        currentProductLeftPercent: parseSimplePercent(
          generator.currentProductLeft,
        ),
        currentProgressPercent: parseSimplePercent(generator.currentProgress),
      };

      return {
        ...enrichedGenerator,
        isDiesel: isDieselGenerator(enrichedGenerator),
        isMechSource: isMechanicalSource(enrichedGenerator),
        isMechConsumer: isMechanicalConsumer(enrichedGenerator),
        isElectrical: isElectricalGenerator(enrichedGenerator),
      };
    });

    const issueCount = generators.filter(
      (generator) => generator.status !== "OK",
    ).length;
    const dieselGenerators = generators.filter(
      (generator) => generator.isDiesel,
    );
    const nonDieselGenerators = generators.filter(
      (generator) => !generator.isDiesel,
    );
    const mechSources = generators.filter(
      (generator) => generator.isMechSource,
    );
    const mechConsumers = generators.filter(
      (generator) => generator.isMechConsumer,
    );
    const electricalGenerators = generators.filter(
      (generator) => generator.isElectrical,
    );

    const totalListedCapacity = generators.reduce(
      (total, generator) => total + (generator.maxCapacity || 0),
      0,
    );
    const totalListedCapacityWithoutDiesel = nonDieselGenerators.reduce(
      (total, generator) => total + (generator.maxCapacity || 0),
      0,
    );
    const dieselBackupCapacity = dieselGenerators.reduce(
      (total, generator) => total + (generator.maxCapacity || 0),
      0,
    );
    const generatedIncludingDiesel = generators.reduce(
      (total, generator) => total + (generator.generated || 0),
      0,
    );
    const generatedWithoutDiesel = nonDieselGenerators.reduce(
      (total, generator) => total + (generator.generated || 0),
      0,
    );
    const mechanicalDemand = mechConsumers.reduce(
      (total, generator) => total + (generator.mechanicalUse || 0),
      0,
    );

    const mechanicalProductionCapacity = mechSources.reduce(
      (total, generator) => total + (generator.mechanicalCapacity || 0),
      0,
    );

    const mechanicalProductionCurrent = mechSources.reduce(
      (total, generator) => total + (generator.mechanicalCurrent || 0),
      0,
    );

    return {
      success: true,
      generatorCount: generators.length,
      issueCount,
      dieselGeneratorCount: dieselGenerators.length,
      mechSourceCount: mechSources.length,
      mechConsumerCount: mechConsumers.length,
      electricalGeneratorCount: electricalGenerators.length,
      totalListedCapacity,
      totalListedCapacityWithoutDiesel,
      dieselBackupCapacity,
      generatedIncludingDiesel,
      generatedWithoutDiesel,
      mechanicalDemand,
      mechanicalProductionCapacity,
      mechanicalProductionCurrent,
      mechanicalMarginCurrent: mechanicalProductionCurrent - mechanicalDemand,
      generators,
    };
  } catch (error) {
    return {
      success: false,
      error: error.name + ": " + error.message,
      generators: [],
    };
  }
}

function calculateBalance() {
  const recipesExport = readJson(recipesPath);
  const productsExport = readJson(productsPath);
  const worldMachinesExport = readJson(worldMachinesPath);

  const recipes = recipesExport.recipes;
  const products = productsExport.products;
  const machines = (worldMachinesExport.machines ?? []).map(
    normaliseMachineIdentity,
  );

  const recipesById = new Map(recipes.map((recipe) => [recipe.id, recipe]));
  const productsById = new Map(
    products.map((product) => [product.productId, product]),
  );

  function recipeForMachine(recipeId, machineId) {
    const recipe = recipesById.get(recipeId);
    if (recipe && Object.hasOwn(recipe.machineBindings ?? {}, machineId)) {
      return { ...recipe, ...recipe.machineBindings[machineId] };
    }
    return recipe;
  }

  function productName(productId) {
    return productsById.get(productId)?.name ?? productId;
  }

  function productType(productId) {
    return productsById.get(productId)?.type ?? "";
  }

  function isPollutionProduct(productId) {
    const name = productName(productId).toLowerCase();

    return (
      productId === "Product_Virtual_PollutedAir" ||
      name.includes("air pollution") ||
      name.includes("polluted air")
    );
  }

  function isDisposalMachine(machine) {
    const id = String(machine.machineId ?? "").toLowerCase();
    const title = String(machine.title ?? "").toLowerCase();

    return (
      id.includes("smokestack") ||
      id.includes("flare") ||
      id.includes("dump") ||
      id.includes("injection") ||
      title.includes("smoke stack") ||
      title.includes("flare") ||
      title.includes("dump") ||
      title.includes("injection")
    );
  }

  function recipeHasNoOutputs(recipe) {
    return !recipe.outputs || recipe.outputs.length === 0;
  }

  function recipeOutputsOnlyPollution(recipe) {
    const outputs = recipe.outputs ?? [];

    if (outputs.length === 0) {
      return false;
    }

    return outputs.every((output) => isPollutionProduct(output.productId));
  }

  function bufferProductName(buffer) {
    return productName(buffer.productId);
  }

  function blockedOutputBuffersForRecipe(machine, recipe) {
    const buffers = machine.outputBuffers ?? [];
    const outputs = recipe.outputs ?? [];
    const blocked = [];

    for (const output of outputs) {
      const buffer = buffers.find(
        (item) => item.productId === output.productId,
      );

      if (!buffer) {
        continue;
      }

      const requiredSpace = parseQuantity(output.quantity);
      const usableCapacity = parseQuantity(buffer.usableCapacity);
      const quantity = parseQuantity(buffer.quantity);
      const capacity = parseQuantity(buffer.capacity);

      const isBlocked =
        buffer.isFull === true ||
        (requiredSpace > 0 && usableCapacity < requiredSpace);

      if (isBlocked) {
        blocked.push({
          ...buffer,
          requiredSpace,
          usableCapacity,
          quantity,
          capacity,
          blockReason:
            buffer.isFull === true
              ? "buffer full"
              : "not enough free space for next cycle",
        });
      }
    }

    return blocked;
  }

  function missingInputBuffers(machine) {
    return (machine.inputBuffers ?? []).filter((buffer) => {
      const quantity = parseQuantity(buffer.quantity);
      return quantity <= 0;
    });
  }

  function isDisposalUse(machine, recipe) {
    if (recipeHasNoOutputs(recipe)) return true;
    if (isDisposalMachine(machine)) return true;
    if (recipeOutputsOnlyPollution(recipe)) return true;
    return false;
  }

  function getOutputFullDiagnosis(machine, recipe, balanceMap) {
    if (machine.currentState !== "OutputFull") {
      return null;
    }

    const blockedOutputs = blockedOutputBuffersForRecipe(machine, recipe);
    const recipeOutputs = recipe.outputs ?? [];

    if (blockedOutputs.length > 0) {
      const blockedOutputNames = blockedOutputs
        .map((buffer) => {
          const name = bufferProductName(buffer);

          if (buffer.requiredSpace !== undefined) {
            return `${name} (${buffer.quantity}/${buffer.capacity}, needs ${buffer.requiredSpace} free, has ${buffer.usableCapacity})`;
          }

          return name;
        })
        .join(", ");

      const blockedOutputIds = new Set(
        blockedOutputs.map((buffer) => buffer.productId),
      );

      const coOutputs = recipeOutputs
        .filter((output) => !blockedOutputIds.has(output.productId))
        .map((output) => {
          const row = balanceMap.get(output.productId);

          return {
            productId: output.productId,
            name: productName(output.productId),
            hasProductiveDemand:
              row && row.productivelyConsumedCapacity > 0.0001,
            hasDisposal: row && row.disposedCapacity > 0.0001,
            netSupply: row
              ? row.producedCapacity - row.productivelyConsumedCapacity
              : 0,
          };
        });

      const usefulCoOutputs = coOutputs.filter(
        (output) => output.hasProductiveDemand,
      );

      const hasDisposalBlocked = blockedOutputs.some((buffer) => {
        const row = balanceMap.get(buffer.productId);
        return (
          isPollutionProduct(buffer.productId) ||
          (row &&
            row.disposedCapacity > 0.0001 &&
            row.productivelyConsumedCapacity <= 0.0001)
        );
      });

      if (recipeOutputs.length > 1 && usefulCoOutputs.length > 0) {
        const usefulNames = usefulCoOutputs
          .map((output) => output.name)
          .join(", ");

        return {
          level: "bad",
          title: "By-product blockage",
          message:
            "Blocked output buffer: " +
            blockedOutputNames +
            ". " +
            "This may be blocking useful co-product production: " +
            usefulNames +
            ". " +
            "Check consumption, storage, or disposal for: " +
            blockedOutputNames,
        };
      }

      if (recipeOutputs.length > 1) {
        const otherOutputNames = coOutputs
          .map((output) => output.name)
          .join(", ");

        return {
          level: hasDisposalBlocked ? "bad" : "warn",
          title: "Multi-output blockage",
          message:
            "Blocked output buffer: " +
            blockedOutputNames +
            ". " +
            "Recipe also outputs: " +
            otherOutputNames +
            ". " +
            "Check downstream handling for the blocked output.",
        };
      }

      return {
        level: hasDisposalBlocked ? "bad" : "warn",
        title: "Output full",
        message: "Blocked output buffer: " + blockedOutputNames,
      };
    }

    const outputNames = recipeOutputs
      .map((output) => productName(output.productId))
      .join(", ");

    return {
      level: "warn",
      title: "Output full, cause not identified",
      message:
        "The machine reports OutputFull, but no matching output buffer appears blocked for the next cycle. " +
        "This may be a timing/internal buffer state. Recipe outputs: " +
        outputNames,
    };
  }

  function getNotEnoughInputDiagnosis(machine) {
    if (machine.currentState !== "NotEnoughInput") {
      return null;
    }

    const emptyInputs = missingInputBuffers(machine);

    if (emptyInputs.length > 0) {
      return {
        level: "bad",
        title: "Not enough input",
        message:
          "Missing input buffer: " +
          emptyInputs.map(bufferProductName).join(", "),
      };
    }

    const inputNames = (machine.inputBuffers ?? [])
      .map(
        (buffer) =>
          `${bufferProductName(buffer)} (${buffer.quantity}/${buffer.capacity})`,
      )
      .join(", ");

    return {
      level: "warn",
      title: "Not enough input, buffer partially filled",
      message:
        "The machine reports NotEnoughInput, but no input buffer is empty. " +
        "Current input buffers: " +
        inputNames,
    };
  }

  function ensureProductRow(balanceMap, productId) {
    if (!balanceMap.has(productId)) {
      balanceMap.set(productId, {
        productId,
        name: productName(productId),
        type: productType(productId),

        producedCapacity: 0,
        productivelyConsumedCapacity: 0,
        disposedCapacity: 0,
        netCapacity: 0,

        producedCurrent: 0,
        productivelyConsumedCurrent: 0,
        disposedCurrent: 0,
        netCurrent: 0,

        producers: [],
        productConsumers: [],
        disposers: [],
      });
    }

    return balanceMap.get(productId);
  }

  function addProduction(
    balanceMap,
    productId,
    capacityAmount,
    currentAmount,
    machine,
    recipe,
  ) {
    const row = ensureProductRow(balanceMap, productId);

    row.producedCapacity += capacityAmount;
    row.producedCurrent += currentAmount;
    row.netCapacity += capacityAmount;
    row.netCurrent += currentAmount;

    row.producers.push({
      capacityAmount,
      currentAmount,
      machine,
      machineId: machine.machineId,
      entityId: machine.entityId,
      title: machine.title,
      customTitle: machine.customTitle || "",
      displayTitle: machine.displayTitle || machine.title,
      state: machine.currentState,
      utilization: machine.utilization,
      recipeId: recipe.id,
      isPaused: machine.isPaused === true,
      diagnosis: null,
    });
  }

  function addProductiveConsumption(
    balanceMap,
    productId,
    capacityAmount,
    currentAmount,
    machine,
    recipe,
  ) {
    const row = ensureProductRow(balanceMap, productId);

    row.productivelyConsumedCapacity += capacityAmount;
    row.productivelyConsumedCurrent += currentAmount;
    row.netCapacity -= capacityAmount;
    row.netCurrent -= currentAmount;

    row.productConsumers.push({
      capacityAmount,
      currentAmount,
      machine,
      machineId: machine.machineId,
      entityId: machine.entityId,
      title: machine.title,
      customTitle: machine.customTitle || "",
      displayTitle: machine.displayTitle || machine.title,
      state: machine.currentState,
      utilization: machine.utilization,
      recipeId: recipe.id,
      isPaused: machine.isPaused === true,
      diagnosis: null,
    });
  }

  function addDisposal(
    balanceMap,
    productId,
    capacityAmount,
    currentAmount,
    machine,
    recipe,
  ) {
    const row = ensureProductRow(balanceMap, productId);

    row.disposedCapacity += capacityAmount;
    row.disposedCurrent += currentAmount;
    row.netCapacity -= capacityAmount;
    row.netCurrent -= currentAmount;

    row.disposers.push({
      capacityAmount,
      currentAmount,
      machine,
      machineId: machine.machineId,
      entityId: machine.entityId,
      title: machine.title,
      customTitle: machine.customTitle || "",
      displayTitle: machine.displayTitle || machine.title,
      state: machine.currentState,
      utilization: machine.utilization,
      recipeId: recipe.id,
      isPaused: machine.isPaused === true,
      diagnosis: null,
    });
  }

  function hasDisposal(row) {
    return row.disposedCapacity > 0.0001;
  }

  function hasProductiveUse(row) {
    return row.productivelyConsumedCapacity > 0.0001;
  }

  function isPureDisposalRow(row) {
    return hasDisposal(row) && !hasProductiveUse(row);
  }

  function isOverflowDisposalRow(row) {
    return hasDisposal(row) && hasProductiveUse(row);
  }

  function netSupplyCapacity(row) {
    return row.producedCapacity - row.productivelyConsumedCapacity;
  }

  function currentNetSupply(row) {
    return row.producedCurrent - row.productivelyConsumedCurrent;
  }

  function spareDisposalCapacity(row) {
    return row.disposedCapacity - row.producedCapacity;
  }

  function unmanagedCurrent(row) {
    return row.producedCurrent - row.disposedCurrent;
  }

  function overflowAmountCapacity(row) {
    return Math.max(0, row.producedCapacity - row.productivelyConsumedCapacity);
  }

  function overflowAmountCurrent(row) {
    return Math.max(0, row.producedCurrent - row.productivelyConsumedCurrent);
  }

  function spareOverflowDisposalCapacity(row) {
    return row.disposedCapacity - overflowAmountCapacity(row);
  }

  function spareOverflowDisposalCurrent(row) {
    return row.disposedCurrent - overflowAmountCurrent(row);
  }

  function rowHasProducerIssue(row) {
    return (row.producers ?? []).some(
      (contribution) =>
        contribution.diagnosis ||
        String(contribution.state || "")
          .toLowerCase()
          .includes("outputfull") ||
        String(contribution.state || "")
          .toLowerCase()
          .includes("notenoughinput"),
    );
  }

  function rowHasBlockedProducers(row) {
    return (row.producers ?? []).some((contribution) =>
      String(contribution.state || "")
        .toLowerCase()
        .includes("outputfull"),
    );
  }

  function rowHasInputStarvedProducers(row) {
    return (row.producers ?? []).some((contribution) =>
      String(contribution.state || "")
        .toLowerCase()
        .includes("notenoughinput"),
    );
  }

  function statusFor(row) {
    const epsilon = 0.0001;

    if (isPollutionProduct(row.productId) && row.producedCapacity > epsilon) {
      return "POLLUTION GENERATED";
    }

    if (isPureDisposalRow(row)) {
      const spare = spareDisposalCapacity(row);
      const unmanaged = unmanagedCurrent(row);

      if (spare < -epsilon) {
        return "DISPOSAL CAPACITY SHORTFALL";
      }

      if (unmanaged > epsilon || rowHasBlockedProducers(row)) {
        return "CAPACITY OK, CHECK FLOW";
      }

      return "DISPOSAL OK";
    }

    if (isOverflowDisposalRow(row)) {
      const netSupply = netSupplyCapacity(row);
      const currentNet = currentNetSupply(row);

      if (netSupply < -epsilon) {
        return "SHORTFALL";
      }

      const spareOverflow = spareOverflowDisposalCapacity(row);

      if (spareOverflow < -epsilon) {
        return "EXCESS DISPOSAL SHORTFALL";
      }

      if (netSupply > epsilon) {
        if (
          row.productivelyConsumedCapacity > epsilon &&
          currentNet <= epsilon &&
          rowHasBlockedProducers(row)
        ) {
          return "CAPACITY OK, PRODUCERS BLOCKED";
        }

        if (
          row.productivelyConsumedCapacity > epsilon &&
          currentNet <= epsilon &&
          rowHasInputStarvedProducers(row)
        ) {
          return "CAPACITY OK, PRODUCERS STARVED";
        }

        return "SURPLUS, OVERFLOW COVERED";
      }

      if (rowHasProducerIssue(row)) {
        return "BALANCED, PRODUCER ISSUE";
      }

      return "BALANCED";
    }

    if (
      row.producedCapacity <= epsilon &&
      row.productivelyConsumedCapacity > epsilon
    ) {
      return "EXTERNAL INPUT REQUIRED";
    }

    const netSupply = netSupplyCapacity(row);
    const currentNet = currentNetSupply(row);

    if (netSupply < -epsilon) {
      return "SHORTFALL";
    }

    if (netSupply > epsilon) {
      if (
        row.productivelyConsumedCapacity > epsilon &&
        currentNet <= epsilon &&
        rowHasBlockedProducers(row)
      ) {
        return "CAPACITY OK, PRODUCERS BLOCKED";
      }

      if (
        row.productivelyConsumedCapacity > epsilon &&
        currentNet <= epsilon &&
        rowHasInputStarvedProducers(row)
      ) {
        return "CAPACITY OK, PRODUCERS STARVED";
      }

      return "SURPLUS";
    }

    if (rowHasProducerIssue(row)) {
      return "BALANCED, PRODUCER ISSUE";
    }

    return "BALANCED";
  }

  const balance = new Map();
  const skippedMachines = [];

  const activeMachines = machines.filter(
    (machine) =>
      machine.isConstructed === true &&
      (machine.isEnabled === true || machine.isPaused === true),
  );

  for (const machine of activeMachines) {
    const recipeId = getSelectedRecipeId(machine);

    if (!recipeId) {
      skippedMachines.push({
        machineId: machine.machineId,
        entityId: machine.entityId,
        title: machine.title,
        customTitle: machine.customTitle || "",
        displayTitle: machine.displayTitle || machine.title,
        reason: "No selected/assigned recipe found",
      });
      continue;
    }

    const recipe = recipeForMachine(recipeId, machine.machineId);

    if (!recipe) {
      skippedMachines.push({
        machineId: machine.machineId,
        entityId: machine.entityId,
        title: machine.title,
        customTitle: machine.customTitle || "",
        displayTitle: machine.displayTitle || machine.title,
        recipeId,
        reason: "Recipe not found in catalogue",
      });
      continue;
    }

    const durationTicks = parseDurationTicks(recipe.duration);

    if (durationTicks <= 0) {
      skippedMachines.push({
        machineId: machine.machineId,
        entityId: machine.entityId,
        title: machine.title,
        customTitle: machine.customTitle || "",
        displayTitle: machine.displayTitle || machine.title,
        recipeId,
        reason: "Recipe duration invalid",
      });
      continue;
    }

    const cyclesPerMinute = TICKS_PER_MINUTE / durationTicks;
    const isPaused = machine.isPaused === true;
    const utilizationFactor = isPaused ? 0 : parsePercent(machine.utilization);
    const disposalUse = isDisposalUse(machine, recipe);

    for (const input of recipe.inputs ?? []) {
      const quantity = parseQuantity(input.quantity);
      const capacityAmount = quantity * cyclesPerMinute;
      const currentAmount = capacityAmount * utilizationFactor;

      if (disposalUse) {
        addDisposal(
          balance,
          input.productId,
          capacityAmount,
          currentAmount,
          machine,
          recipe,
        );
      } else {
        addProductiveConsumption(
          balance,
          input.productId,
          capacityAmount,
          currentAmount,
          machine,
          recipe,
        );
      }
    }

    for (const output of recipe.outputs ?? []) {
      const quantity = parseQuantity(output.quantity);
      const capacityAmount = quantity * cyclesPerMinute;
      const currentAmount = capacityAmount * utilizationFactor;

      addProduction(
        balance,
        output.productId,
        capacityAmount,
        currentAmount,
        machine,
        recipe,
      );
    }
  }

  for (const row of balance.values()) {
    for (const contribution of row.producers) {
      const recipe = recipeForMachine(contribution.recipeId, contribution.machineId);
      const machine = contribution.machine;

      if (!recipe || !machine) {
        continue;
      }

      contribution.diagnosis =
        getOutputFullDiagnosis(machine, recipe, balance) ??
        getNotEnoughInputDiagnosis(machine);
    }

    for (const contribution of row.productConsumers) {
      const machine = contribution.machine;
      contribution.diagnosis = machine
        ? getNotEnoughInputDiagnosis(machine)
        : null;
    }

    for (const contribution of row.disposers) {
      const machine = contribution.machine;
      contribution.diagnosis = machine
        ? getNotEnoughInputDiagnosis(machine)
        : null;
    }
  }

  const allRows = Array.from(balance.values())
    .map((row) => {
      const disposal = hasDisposal(row);
      const pureDisposal = isPureDisposalRow(row);
      const overflowDisposal = isOverflowDisposalRow(row);

      return {
        ...row,

        hasDisposal: disposal,
        isPureDisposal: pureDisposal,
        isOverflowDisposal: overflowDisposal,

        displayNetCapacity: netSupplyCapacity(row),
        displayNetCurrent: currentNetSupply(row),

        spareDisposalCapacity: spareDisposalCapacity(row),
        unmanagedCurrent: unmanagedCurrent(row),

        overflowAmountCapacity: overflowAmountCapacity(row),
        overflowAmountCurrent: overflowAmountCurrent(row),
        spareOverflowDisposalCapacity: spareOverflowDisposalCapacity(row),
        spareOverflowDisposalCurrent: spareOverflowDisposalCurrent(row),

        status: statusFor(row),
        isPollution: isPollutionProduct(row.productId),
      };
    })
    .sort((a, b) => a.name.localeCompare(b.name));

  const pollutionRows = allRows.filter((row) => row.isPollution);

  const currentAirPollutionRate = pollutionRows.reduce(
    (total, row) => total + Math.max(0, Number(row.producedCurrent || 0)),
    0,
  );

  const rows = allRows.filter((row) => !row.isPollution);
  const rowsById = new Map(allRows.map((row) => [row.productId, row]));

  function stateCountsLabel(stateCounts) {
    return Object.entries(stateCounts)
      .sort((a, b) => b[1] - a[1])
      .map(([state, count]) => `${count} ${state || "Unknown"}`)
      .join(", ");
  }

  function buildDependencyForRow(row) {
    const inputTotals = new Map();
    const producerGroups = new Map();

    for (const contribution of row.producers ?? []) {
      const recipe = recipeForMachine(contribution.recipeId, contribution.machineId);

      if (!recipe) {
        continue;
      }

      const durationTicks = parseDurationTicks(recipe.duration);

      if (durationTicks <= 0) {
        continue;
      }

      const cyclesPerMinute = TICKS_PER_MINUTE / durationTicks;
      const groupTitle =
        contribution.title || contribution.machineId || "Machine";
      const groupKey = `${contribution.recipeId}|${groupTitle}`;

      if (!producerGroups.has(groupKey)) {
        producerGroups.set(groupKey, {
          recipeId: contribution.recipeId,
          title: groupTitle,
          machineId: contribution.machineId,
          machineCount: 0,
          capacityAmount: 0,
          currentAmount: 0,
          pausedCount: 0,
          stateCounts: {},
        });
      }

      const group = producerGroups.get(groupKey);
      const state = contribution.state || "Unknown";

      group.machineCount += 1;
      group.capacityAmount += Number(contribution.capacityAmount || 0);
      group.currentAmount += Number(contribution.currentAmount || 0);

      if (contribution.isPaused === true) {
        group.pausedCount += 1;
      }

      group.stateCounts[state] = (group.stateCounts[state] || 0) + 1;

      for (const input of recipe.inputs ?? []) {
        const productId = input.productId;
        const requiredAmount = parseQuantity(input.quantity) * cyclesPerMinute;

        if (!productId || requiredAmount <= 0) {
          continue;
        }

        if (!inputTotals.has(productId)) {
          inputTotals.set(productId, {
            productId,
            name: productName(productId),
            type: productType(productId),
            requiredHere: 0,
          });
        }

        inputTotals.get(productId).requiredHere += requiredAmount;
      }
    }

    const inputs = Array.from(inputTotals.values())
      .map((input) => {
        const inputRow = rowsById.get(input.productId);

        const totalFactoryDemand = inputRow
          ? Number(inputRow.productivelyConsumedCapacity || 0)
          : 0;

        const canProduce = inputRow
          ? Number(inputRow.producedCapacity || 0)
          : 0;
        const currentProduction = inputRow
          ? Number(inputRow.producedCurrent || 0)
          : 0;

        const netCapacity = inputRow
          ? Number(inputRow.displayNetCapacity || 0)
          : 0 - input.requiredHere;

        let status = inputRow ? inputRow.status : "NO PRODUCER";

        if (canProduce <= 0 && input.requiredHere > 0) {
          status = "NO PRODUCER";
        } else if (netCapacity < -0.0001) {
          status = "SHORTFALL";
        }

        return {
          ...input,
          requiredHere: Number(input.requiredHere || 0),
          totalFactoryDemand,
          canProduce,
          currentProduction,
          netCapacity,
          status,
        };
      })
      .sort((a, b) => {
        const statusCompare = String(a.status).localeCompare(String(b.status));

        if (statusCompare !== 0) {
          return statusCompare;
        }

        return b.requiredHere - a.requiredHere;
      });

    const groups = Array.from(producerGroups.values())
      .map((group) => ({
        ...group,
        stateSummary: stateCountsLabel(group.stateCounts),
      }))
      .sort((a, b) => b.capacityAmount - a.capacityAmount);

    return {
      productId: row.productId,
      name: row.name,
      type: row.type,
      status: row.status,
      producedCapacity: row.producedCapacity,
      producedCurrent: row.producedCurrent,
      totalFactoryDemand: row.productivelyConsumedCapacity,
      netCapacity: row.displayNetCapacity,
      producerGroups: groups,
      inputs,
    };
  }

  const dependencies = Object.fromEntries(
    rows.map((row) => [row.productId, buildDependencyForRow(row)]),
  );

  return {
    metadata: {
      worldMachineCount: machines.length,
      activeMachineCount: activeMachines.length,
      currentAirPollutionRate,
      snapshotTrigger: worldMachinesExport.trigger,
      snapshotExportCount: worldMachinesExport.exportCount,
      ticksPerMinute: TICKS_PER_MINUTE,
      updatedAt: new Date().toISOString(),
    },
    power: calculatePowerSummary(),
    powerEntities: calculatePowerEntitiesSummary(),
    rows,
    dependencies,
    machines,
    skippedMachines,
  };
}

function sendJson(res, data, statusCode = 200) {
  res.writeHead(statusCode, {
    "Content-Type": "application/json",
    "Access-Control-Allow-Origin": "*",
  });

  res.end(JSON.stringify(data, null, 2));
}

function sendFile(res, filePath, contentType) {
  fs.readFile(filePath, (error, content) => {
    if (error) {
      res.writeHead(404);
      res.end("Not found");
      return;
    }

    res.writeHead(200, { "Content-Type": contentType });
    res.end(content);
  });
}

const server = http.createServer((req, res) => {
  try {
    if (req.url === "/api/balance") {
      sendJson(res, calculateBalance());
      return;
    }

    if (req.url === "/" || req.url === "/index.html") {
      sendFile(res, path.join(__dirname, "public", "index.html"), "text/html");
      return;
    }

    res.writeHead(404);
    res.end("Not found");
  } catch (error) {
    sendJson(
      res,
      {
        success: false,
        error: error.name + ": " + error.message,
      },
      500,
    );
  }
});

server.listen(PORT, () => {
  console.log(`Captains Log dashboard running at http://localhost:${PORT}`);
  console.log(`Reading exports from: ${exportFolder}`);
});
