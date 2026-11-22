// Provenance-aware gate between drawing/project inputs and the standalone GasCalc engine.
// This layer has no AutoCAD dependency and performs no DWG or file I/O.
using System;
using System.Collections.Generic;
using System.Globalization;
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
                outcome.BlockingFindings.Add(field + ": EVIDENCE_METADATA_INVALID");
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
