// Provenance-aware gate between drawing/project inputs and the standalone GasCalc engine.
// This layer has no AutoCAD dependency and performs no DWG or file I/O.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AGPT.Calc
{
    public enum InputEvidenceStatus
    {
        Missing,
        Unconfirmed,
        EngineerEntered,
        ImportedVerified,
        ProjectSetting,
        NormativeReference,
        ManufacturerDatasheet,
        NotApplicableConfirmed
    }

    public sealed class InputEvidence
    {
        public InputEvidenceStatus Status { get; set; }
        public string SourceRef { get; set; }
        public string SourceSha256 { get; set; }
        public string SourceVersion { get; set; }
        public string ActorId { get; set; }
        // SHA-256 of the exact canonical field value, not merely the source document.
        public string ValueSha256 { get; set; }
    }

    public sealed class GasCalculationRequest
    {
        public RoomInput Room { get; set; }
        public ProjectSettings Project { get; set; }
        public GotvSpec Gotv { get; set; }
        public List<Module> Catalog { get; set; }
        public IDictionary<string, InputEvidence> Evidence { get; set; }
            = new Dictionary<string, InputEvidence>(StringComparer.Ordinal);
    }

    public sealed class GasCalculationOutcome
    {
        public CalcResult Calculation { get; internal set; }
        public List<string> BlockingFindings { get; } = new List<string>();
        public List<string> ReviewFindings { get; } = new List<string>();

        public bool IsBlocked => BlockingFindings.Count > 0;

        // Strict default: any engine warning or unresolved module decision prevents release.
        public bool CanRelease =>
            !IsBlocked &&
            Calculation != null &&
            !Calculation.NeedsEngineerDecision &&
            Calculation.Warnings.Count == 0;
    }

    /// <summary>
    /// Validates input provenance before the numerical engine is called.
    /// Valid numeric defaults are not proof that an engineer supplied or approved the values.
    /// </summary>
    public static class GasCalculationService
    {
        public static class Fields
        {
            public const string RoomName = "Room.Name";
            public const string RoomVolume = "Room.Volume";
            public const string RoomHeight = "Room.Height";
            public const string OpeningsArea = "Room.OpeningsArea";
            public const string DeltaVolume = "Room.DeltaVolume";
            public const string OpeningsPosition = "Room.OpeningsPosition";
            public const string MinTemperature = "Room.TMinC";
            public const string FireKind = "Room.Fire";
            public const string PipeVolume = "Room.PipeVolume";
            public const string ResidualAbsPressure = "Room.PResidualAbsMpa";
            public const string ChosenModules = "Room.ChosenModules";

            public const string Altitude = "Project.AltitudeM";
            public const string K3Mode = "Project.K3Mode";

            public const string GasName = "Gotv.Name";
            public const string GasKind = "Gotv.Kind";
            public const string GasReferenceDensity = "Gotv.Rho0";
            public const string GasConcentration = "Gotv.CnHeptane";
            public const string GasInjectionTime = "Gotv.TauPod";
            public const string GasVaporDensityCap = "Gotv.VaporDensityCap";
            public const string GasLiquidDensity = "Gotv.LiquidDensity";

            public static string CatalogName(int index) => "Catalog[" + index.ToString(CultureInfo.InvariantCulture) + "].Name";
            public static string CatalogFill(int index) => "Catalog[" + index.ToString(CultureInfo.InvariantCulture) + "].FillKg";
            public static string CatalogResidual(int index) => "Catalog[" + index.ToString(CultureInfo.InvariantCulture) + "].ResidualKg";
        }

        private static readonly Regex Sha256Pattern =
            new Regex("^[0-9a-fA-F]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static bool HasText(string value) => !string.IsNullOrWhiteSpace(value);

        private static bool IsSha256(string value) =>
            !string.IsNullOrWhiteSpace(value) && Sha256Pattern.IsMatch(value);

        private static string Canonical(string kind, string value)
        {
            if (value == null) return kind + ":<null>";
            return kind + ":" + value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;
        }

        private static string Number(double value) =>
            value.ToString("R", CultureInfo.InvariantCulture);

        private static string NullableNumber(double? value) =>
            value.HasValue ? Canonical("double", Number(value.Value)) : Canonical("double", null);

        private static string HashCanonicalValue(string field, string canonical)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(field + "\n" + canonical);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        private static string CanonicalValue(GasCalculationRequest request, string field)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Room == null && field.StartsWith("Room.", StringComparison.Ordinal))
                throw new ArgumentException("ROOM_INPUT_REQUIRED", nameof(request));

            RoomInput room = request.Room;
            if (field == Fields.RoomName) return Canonical("string", room.Name);
            if (field == Fields.RoomVolume) return Canonical("double", Number(room.Volume));
            if (field == Fields.RoomHeight) return Canonical("double", Number(room.Height));
            if (field == Fields.OpeningsArea) return Canonical("double", Number(room.OpeningsArea));
            if (field == Fields.DeltaVolume) return NullableNumber(room.DeltaVolume);
            if (field == Fields.OpeningsPosition) return Canonical("enum:OpeningsPos", ((int)room.OpeningsPosition).ToString(CultureInfo.InvariantCulture));
            if (field == Fields.MinTemperature) return Canonical("double", Number(room.TMinC));
            if (field == Fields.FireKind) return Canonical("enum:FireKind", ((int)room.Fire).ToString(CultureInfo.InvariantCulture));
            if (field == Fields.PipeVolume) return Canonical("double", Number(room.PipeVolume));
            if (field == Fields.ResidualAbsPressure) return NullableNumber(room.PResidualAbsMpa);
            if (field == Fields.ChosenModules)
            {
                if (room.ChosenModules == null) return Canonical("string-list", null);
                StringBuilder list = new StringBuilder();
                foreach (string name in room.ChosenModules)
                    list.Append(name == null ? "<null>" : name.Length.ToString(CultureInfo.InvariantCulture) + ":" + name).Append("|");
                return Canonical("string-list", list.ToString());
            }

            if (field == Fields.Altitude)
            {
                if (request.Project == null) throw new ArgumentException("PROJECT_SETTINGS_REQUIRED", nameof(request));
                return Canonical("double", Number(request.Project.AltitudeM));
            }
            if (field == Fields.K3Mode)
            {
                if (request.Project == null) throw new ArgumentException("PROJECT_SETTINGS_REQUIRED", nameof(request));
                return Canonical("enum:K3Mode", ((int)request.Project.K3Mode).ToString(CultureInfo.InvariantCulture));
            }

            if (field == Fields.GasName || field == Fields.GasKind ||
                field == Fields.GasReferenceDensity || field == Fields.GasConcentration ||
                field == Fields.GasInjectionTime || field == Fields.GasVaporDensityCap ||
                field == Fields.GasLiquidDensity)
            {
                if (request.Gotv == null) throw new ArgumentException("GOTV_SPEC_REQUIRED", nameof(request));
                if (field == Fields.GasName) return Canonical("string", request.Gotv.Name);
                if (field == Fields.GasKind) return Canonical("enum:GasKind", ((int)request.Gotv.Kind).ToString(CultureInfo.InvariantCulture));
                if (field == Fields.GasReferenceDensity) return Canonical("double", Number(request.Gotv.Rho0));
                if (field == Fields.GasConcentration) return Canonical("double", Number(request.Gotv.CnHeptane));
                if (field == Fields.GasInjectionTime) return Canonical("double", Number(request.Gotv.TauPod));
                if (field == Fields.GasVaporDensityCap) return Canonical("double", Number(request.Gotv.VaporDensityCap));
                return Canonical("double", Number(request.Gotv.LiquidDensity));
            }

            if (field.StartsWith("Catalog[", StringComparison.Ordinal))
            {
                if (request.Catalog == null) throw new ArgumentException("VERIFIED_CATALOG_REQUIRED", nameof(request));
                int close = field.IndexOf(']');
                int open = field.IndexOf('[');
                int index;
                if (open < 0 || close <= open + 1 ||
                    !int.TryParse(field.Substring(open + 1, close - open - 1), NumberStyles.None,
                        CultureInfo.InvariantCulture, out index) ||
                    index < 0 || index >= request.Catalog.Count || request.Catalog[index] == null)
                    throw new ArgumentOutOfRangeException(nameof(field), "CATALOG_INDEX_INVALID");
                Module module = request.Catalog[index];
                if (field == Fields.CatalogName(index)) return Canonical("string", module.Name);
                if (field == Fields.CatalogFill(index)) return Canonical("double", Number(module.FillKg));
                if (field == Fields.CatalogResidual(index)) return Canonical("double", Number(module.ResidualKg));
            }

            throw new ArgumentException("UNKNOWN_EVIDENCE_FIELD: " + field, nameof(field));
        }

        /// <summary>
        /// Computes the binding hash for one exact value in a request.
        /// Integrators must recalculate it whenever that value changes.
        /// </summary>
        public static string ComputeEvidenceValueSha256(GasCalculationRequest request, string field)
        {
            if (string.IsNullOrWhiteSpace(field)) throw new ArgumentException("EVIDENCE_FIELD_REQUIRED", nameof(field));
            return HashCanonicalValue(field, CanonicalValue(request, field));
        }

        private static void Require(
            GasCalculationOutcome outcome,
            GasCalculationRequest request,
            string field,
            params InputEvidenceStatus[] allowed)
        {
            if (request.Evidence == null ||
                !request.Evidence.TryGetValue(field, out InputEvidence evidence) ||
                evidence == null)
            {
                outcome.BlockingFindings.Add(field + ": EVIDENCE_MISSING");
                return;
            }

            bool statusAllowed = false;
            foreach (InputEvidenceStatus status in allowed)
            {
                if (evidence.Status == status)
                {
                    statusAllowed = true;
                    break;
                }
            }

            if (!statusAllowed ||
                evidence.Status == InputEvidenceStatus.Missing ||
                evidence.Status == InputEvidenceStatus.Unconfirmed)
            {
                outcome.BlockingFindings.Add(field + ": EVIDENCE_STATUS_NOT_ACCEPTED (" + evidence.Status + ")");
                return;
            }

            bool metadataValid;
            switch (evidence.Status)
            {
                case InputEvidenceStatus.EngineerEntered:
                case InputEvidenceStatus.NotApplicableConfirmed:
                    metadataValid = HasText(evidence.ActorId);
                    break;
                case InputEvidenceStatus.ImportedVerified:
                case InputEvidenceStatus.ManufacturerDatasheet:
                    metadataValid = HasText(evidence.SourceRef) && IsSha256(evidence.SourceSha256);
                    break;
                case InputEvidenceStatus.ProjectSetting:
                case InputEvidenceStatus.NormativeReference:
                    metadataValid = HasText(evidence.SourceRef) && HasText(evidence.SourceVersion);
                    break;
                default:
                    metadataValid = false;
                    break;
            }

            if (!metadataValid)
            {
                outcome.BlockingFindings.Add(field + ": EVIDENCE_METADATA_INVALID");
                return;
            }

            if (!IsSha256(evidence.ValueSha256))
            {
                outcome.BlockingFindings.Add(field + ": VALUE_SHA256_MISSING_OR_INVALID");
                return;
            }

            string expectedValueHash;
            try
            {
                expectedValueHash = ComputeEvidenceValueSha256(request, field);
            }
            catch (ArgumentException)
            {
                outcome.BlockingFindings.Add(field + ": VALUE_CANONICALIZATION_FAILED");
                return;
            }

            if (!string.Equals(evidence.ValueSha256, expectedValueHash, StringComparison.OrdinalIgnoreCase))
                outcome.BlockingFindings.Add(field + ": VALUE_SHA256_MISMATCH");
        }

        private static void RequireRoomField(
            GasCalculationOutcome outcome,
            GasCalculationRequest request,
            string field)
        {
            Require(outcome, request, field,
                InputEvidenceStatus.EngineerEntered,
                InputEvidenceStatus.ImportedVerified);
        }

        private static void RequireManufacturerField(
            GasCalculationOutcome outcome,
            GasCalculationRequest request,
            string field)
        {
            Require(outcome, request, field, InputEvidenceStatus.ManufacturerDatasheet);
        }

        private static void ValidateProvenance(GasCalculationOutcome outcome, GasCalculationRequest request)
        {
            if (request.Room == null)
            {
                outcome.BlockingFindings.Add("Room: ROOM_INPUT_REQUIRED");
                return;
            }
            if (request.Project == null)
                outcome.BlockingFindings.Add("Project: PROJECT_SETTINGS_REQUIRED");
            if (request.Gotv == null)
                outcome.BlockingFindings.Add("Gotv: GOTV_SPEC_REQUIRED");
            if (request.Catalog == null || request.Catalog.Count == 0)
                outcome.BlockingFindings.Add("Catalog: VERIFIED_CATALOG_REQUIRED");

            RequireRoomField(outcome, request, Fields.RoomName);
            RequireRoomField(outcome, request, Fields.RoomVolume);
            RequireRoomField(outcome, request, Fields.RoomHeight);
            RequireRoomField(outcome, request, Fields.OpeningsArea);
            RequireRoomField(outcome, request, Fields.OpeningsPosition);
            RequireRoomField(outcome, request, Fields.MinTemperature);
            RequireRoomField(outcome, request, Fields.FireKind);
            RequireRoomField(outcome, request, Fields.PipeVolume);

            if (request.Room.DeltaVolume.HasValue)
                RequireRoomField(outcome, request, Fields.DeltaVolume);
            else
                Require(outcome, request, Fields.DeltaVolume,
                    InputEvidenceStatus.NotApplicableConfirmed);

            if (request.Room.PipeVolume > 0.0)
                Require(outcome, request, Fields.ResidualAbsPressure,
                    InputEvidenceStatus.ImportedVerified);

            if (request.Room.ChosenModules != null && request.Room.ChosenModules.Count > 0)
                Require(outcome, request, Fields.ChosenModules,
                    InputEvidenceStatus.EngineerEntered,
                    InputEvidenceStatus.ImportedVerified);

            if (request.Project != null)
            {
                Require(outcome, request, Fields.Altitude, InputEvidenceStatus.ProjectSetting);
                Require(outcome, request, Fields.K3Mode, InputEvidenceStatus.ProjectSetting);
            }

            if (request.Gotv != null)
            {
                RequireManufacturerField(outcome, request, Fields.GasName);
                Require(outcome, request, Fields.GasKind, InputEvidenceStatus.NormativeReference);
                RequireManufacturerField(outcome, request, Fields.GasReferenceDensity);
                Require(outcome, request, Fields.GasConcentration, InputEvidenceStatus.NormativeReference);
                Require(outcome, request, Fields.GasInjectionTime, InputEvidenceStatus.NormativeReference);
                RequireManufacturerField(outcome, request, Fields.GasVaporDensityCap);
                RequireManufacturerField(outcome, request, Fields.GasLiquidDensity);
            }

            if (request.Catalog != null)
            {
                for (int i = 0; i < request.Catalog.Count; i++)
                {
                    RequireManufacturerField(outcome, request, Fields.CatalogName(i));
                    RequireManufacturerField(outcome, request, Fields.CatalogFill(i));
                    RequireManufacturerField(outcome, request, Fields.CatalogResidual(i));
                }
            }
        }

        /// <summary>
        /// Returns a blocked outcome without calling GasCalc when evidence is incomplete.
        /// No defaults are substituted by this service.
        /// </summary>
        public static GasCalculationOutcome Calculate(GasCalculationRequest request)
        {
            GasCalculationOutcome outcome = new GasCalculationOutcome();
            if (request == null)
            {
                outcome.BlockingFindings.Add("Request: CALCULATION_REQUEST_REQUIRED");
                return outcome;
            }

            ValidateProvenance(outcome, request);
            if (outcome.IsBlocked)
                return outcome;

            if (request.Room.PipeVolume > 0.0 && !request.Room.PResidualAbsMpa.HasValue)
            {
                outcome.BlockingFindings.Add(Fields.ResidualAbsPressure + ": REQUIRED_WHEN_PIPE_VOLUME_POSITIVE");
                return outcome;
            }

            try
            {
                // Explicit non-null objects are required above; validate again at the numeric boundary.
                GasCalc.ValidateInput(request.Room, request.Project, request.Gotv, request.Catalog);
                outcome.Calculation = GasCalc.Compute(
                    request.Room, request.Project, request.Gotv, request.Catalog);

                if (outcome.Calculation.NeedsEngineerDecision)
                    outcome.ReviewFindings.Add("ENGINEER_MODULE_SELECTION_REQUIRED");

                foreach (string warning in outcome.Calculation.Warnings)
                    outcome.ReviewFindings.Add("ENGINE_REVIEW: " + warning);
            }
            catch (ArgumentException ex)
            {
                outcome.BlockingFindings.Add("GASCALC_INPUT_REJECTED: " + ex.Message);
            }

            return outcome;
        }
    }
}
