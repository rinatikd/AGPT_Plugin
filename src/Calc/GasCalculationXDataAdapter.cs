// Strict, versioned adapter from AGPT XData key/value pairs to the provenance-aware engine.
// This layer is AutoCAD-independent: the host extracts XData inside its read transaction and
// passes the resulting dictionary here. It never invents defaults or writes to a drawing.
using System;
using System.Collections.Generic;
using System.Globalization;
using AGPT.Calc;

namespace AGPT.Calc
{
    public sealed class GasCalculationXDataAdapterResult
    {
        public GasCalculationRequest Request { get; internal set; }
        public List<string> BlockingFindings { get; } = new List<string>();
        public bool IsReady => Request != null && BlockingFindings.Count == 0;
    }

    public static class GasCalculationXDataAdapter
    {
        public const string SchemaKey = "AGPT_CALC_SCHEMA";
        public const string SchemaVersion = "2";
        public const string CatalogCountKey = "Catalog.Count";
        private const string EvidencePrefix = "EVIDENCE.";

        private static bool TryValue(IDictionary<string, string> data, string key, out string value)
        {
            value = null;
            return data != null && data.TryGetValue(key, out value) && value != null;
        }

        private static string RequiredString(
            IDictionary<string, string> data, string key, GasCalculationXDataAdapterResult result)
        {
            string value;
            if (!TryValue(data, key, out value) || string.IsNullOrWhiteSpace(value))
            {
                result.BlockingFindings.Add(key + ": XDATA_VALUE_MISSING");
                return null;
            }
            return value;
        }

        private static double RequiredDouble(
            IDictionary<string, string> data, string key, GasCalculationXDataAdapterResult result)
        {
            string raw;
            double value;
            if (!TryValue(data, key, out raw) ||
                !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                double.IsNaN(value) || double.IsInfinity(value))
            {
                result.BlockingFindings.Add(key + ": XDATA_NUMBER_INVALID_OR_MISSING");
                return 0.0;
            }
            return value;
        }

        private static T RequiredEnum<T>(
            IDictionary<string, string> data, string key, GasCalculationXDataAdapterResult result)
            where T : struct
        {
            string raw;
            T value;
            if (!TryValue(data, key, out raw) ||
                !Enum.TryParse<T>(raw, false, out value) ||
                !Enum.IsDefined(typeof(T), value))
            {
                result.BlockingFindings.Add(key + ": XDATA_ENUM_INVALID_OR_MISSING");
                return default(T);
            }
            return value;
        }

        private static int RequiredCount(
            IDictionary<string, string> data, string key, GasCalculationXDataAdapterResult result,
            int minimum, int maximum)
        {
            string raw;
            int value;
            if (!TryValue(data, key, out raw) ||
                !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value) ||
                value < minimum || value > maximum)
            {
                result.BlockingFindings.Add(key + ": XDATA_COUNT_INVALID");
                return 0;
            }
            return value;
        }

        private static void ReadEvidence(
            IDictionary<string, string> data, GasCalculationRequest request, string field,
            GasCalculationXDataAdapterResult result)
        {
            string prefix = EvidencePrefix + field + ".";
            string rawStatus;
            if (!TryValue(data, prefix + "STATUS", out rawStatus))
                return; // The provenance service reports missing evidence with its canonical field code.

            InputEvidenceStatus status;
            if (!Enum.TryParse<InputEvidenceStatus>(rawStatus, false, out status) ||
                !Enum.IsDefined(typeof(InputEvidenceStatus), status))
            {
                result.BlockingFindings.Add(field + ": XDATA_EVIDENCE_STATUS_INVALID");
                return;
            }

            string sourceRef, sourceHash, sourceVersion, actor, valueHash;
            TryValue(data, prefix + "SOURCE_REF", out sourceRef);
            TryValue(data, prefix + "SOURCE_SHA256", out sourceHash);
            TryValue(data, prefix + "SOURCE_VERSION", out sourceVersion);
            TryValue(data, prefix + "ACTOR_ID", out actor);
            TryValue(data, prefix + "VALUE_SHA256", out valueHash);
            request.Evidence[field] = new InputEvidence
            {
                Status = status,
                SourceRef = sourceRef,
                SourceSha256 = sourceHash,
                SourceVersion = sourceVersion,
                ActorId = actor,
                ValueSha256 = valueHash
            };
        }

        public static GasCalculationXDataAdapterResult Adapt(IDictionary<string, string> data)
        {
            GasCalculationXDataAdapterResult result = new GasCalculationXDataAdapterResult();
            string schema;
            if (!TryValue(data, SchemaKey, out schema))
            {
                result.BlockingFindings.Add("XDATA_SCHEMA_MISSING: legacy XData is not trusted as approved input");
                return result;
            }
            if (!string.Equals(schema, SchemaVersion, StringComparison.Ordinal))
            {
                result.BlockingFindings.Add("XDATA_SCHEMA_UNSUPPORTED: " + schema);
                return result;
            }

            GasCalculationRequest request = new GasCalculationRequest();
            request.Room = new RoomInput
            {
                Name = RequiredString(data, GasCalculationService.Fields.RoomName, result),
                Volume = RequiredDouble(data, GasCalculationService.Fields.RoomVolume, result),
                Height = RequiredDouble(data, GasCalculationService.Fields.RoomHeight, result),
                OpeningsArea = RequiredDouble(data, GasCalculationService.Fields.OpeningsArea, result),
                OpeningsPosition = RequiredEnum<OpeningsPos>(data, GasCalculationService.Fields.OpeningsPosition, result),
                TMinC = RequiredDouble(data, GasCalculationService.Fields.MinTemperature, result),
                Fire = RequiredEnum<FireKind>(data, GasCalculationService.Fields.FireKind, result),
                PipeVolume = RequiredDouble(data, GasCalculationService.Fields.PipeVolume, result)
            };

            string optional;
            double optionalNumber;
            if (TryValue(data, GasCalculationService.Fields.DeltaVolume, out optional))
            {
                if (!double.TryParse(optional, NumberStyles.Float, CultureInfo.InvariantCulture, out optionalNumber) ||
                    double.IsNaN(optionalNumber) || double.IsInfinity(optionalNumber))
                    result.BlockingFindings.Add(GasCalculationService.Fields.DeltaVolume + ": XDATA_NUMBER_INVALID");
                else request.Room.DeltaVolume = optionalNumber;
            }
            if (TryValue(data, GasCalculationService.Fields.ResidualAbsPressure, out optional))
            {
                if (!double.TryParse(optional, NumberStyles.Float, CultureInfo.InvariantCulture, out optionalNumber) ||
                    double.IsNaN(optionalNumber) || double.IsInfinity(optionalNumber))
                    result.BlockingFindings.Add(GasCalculationService.Fields.ResidualAbsPressure + ": XDATA_NUMBER_INVALID");
                else request.Room.PResidualAbsMpa = optionalNumber;
            }

            string chosenCountRaw;
            if (TryValue(data, "Room.ChosenModules.Count", out chosenCountRaw))
            {
                int count;
                if (!int.TryParse(chosenCountRaw, NumberStyles.None, CultureInfo.InvariantCulture, out count) ||
                    count < 1 || count > 20)
                    result.BlockingFindings.Add("Room.ChosenModules.Count: XDATA_COUNT_INVALID");
                else
                {
                    request.Room.ChosenModules = new List<string>();
                    for (int i = 0; i < count; i++)
                        request.Room.ChosenModules.Add(RequiredString(data,
                            "Room.ChosenModules[" + i.ToString(CultureInfo.InvariantCulture) + "]", result));
                }
            }

            request.Project = new ProjectSettings
            {
                AltitudeM = RequiredDouble(data, GasCalculationService.Fields.Altitude, result),
                K3Mode = RequiredEnum<K3Mode>(data, GasCalculationService.Fields.K3Mode, result)
            };
            request.Gotv = new GotvSpec
            {
                Name = RequiredString(data, GasCalculationService.Fields.GasName, result),
                Kind = RequiredEnum<GasKind>(data, GasCalculationService.Fields.GasKind, result),
                Rho0 = RequiredDouble(data, GasCalculationService.Fields.GasReferenceDensity, result),
                CnHeptane = RequiredDouble(data, GasCalculationService.Fields.GasConcentration, result),
                TauPod = RequiredDouble(data, GasCalculationService.Fields.GasInjectionTime, result),
                VaporDensityCap = RequiredDouble(data, GasCalculationService.Fields.GasVaporDensityCap, result),
                LiquidDensity = RequiredDouble(data, GasCalculationService.Fields.GasLiquidDensity, result)
            };

            int catalogCount = RequiredCount(data, CatalogCountKey, result, 1, 20);
            request.Catalog = new List<Module>();
            for (int i = 0; i < catalogCount; i++)
            {
                string name = RequiredString(data, GasCalculationService.Fields.CatalogName(i), result);
                double fill = RequiredDouble(data, GasCalculationService.Fields.CatalogFill(i), result);
                double residual = RequiredDouble(data, GasCalculationService.Fields.CatalogResidual(i), result);
                request.Catalog.Add(new Module(name, fill, residual));
            }

            string[] fields =
            {
                GasCalculationService.Fields.RoomName, GasCalculationService.Fields.RoomVolume,
                GasCalculationService.Fields.RoomHeight, GasCalculationService.Fields.OpeningsArea,
                GasCalculationService.Fields.DeltaVolume, GasCalculationService.Fields.OpeningsPosition,
                GasCalculationService.Fields.MinTemperature, GasCalculationService.Fields.FireKind,
                GasCalculationService.Fields.PipeVolume, GasCalculationService.Fields.ResidualAbsPressure,
                GasCalculationService.Fields.ChosenModules, GasCalculationService.Fields.Altitude,
                GasCalculationService.Fields.K3Mode, GasCalculationService.Fields.GasName,
                GasCalculationService.Fields.GasKind, GasCalculationService.Fields.GasReferenceDensity,
                GasCalculationService.Fields.GasConcentration, GasCalculationService.Fields.GasInjectionTime,
                GasCalculationService.Fields.GasVaporDensityCap, GasCalculationService.Fields.GasLiquidDensity
            };
            foreach (string field in fields) ReadEvidence(data, request, field, result);
            for (int i = 0; i < catalogCount; i++)
            {
                ReadEvidence(data, request, GasCalculationService.Fields.CatalogName(i), result);
                ReadEvidence(data, request, GasCalculationService.Fields.CatalogFill(i), result);
                ReadEvidence(data, request, GasCalculationService.Fields.CatalogResidual(i), result);
            }

            result.Request = request;
            return result;
        }

        // The only supported XData -> calculation entry point. Adapter errors fail closed;
        // the provenance gate and numerical validation run only after schema/value parsing succeeds.
        public static GasCalculationOutcome Calculate(IDictionary<string, string> data)
        {
            GasCalculationXDataAdapterResult adapted = Adapt(data);
            GasCalculationOutcome outcome = new GasCalculationOutcome();
            if (!adapted.IsReady)
            {
                outcome.BlockingFindings.AddRange(adapted.BlockingFindings);
                return outcome;
            }
            return GasCalculationService.Calculate(adapted.Request);
        }
    }
}
