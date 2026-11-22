using System;
using System.Collections.Generic;
using AGPT.Calc;

namespace AGPT.Calc.Tests
{
    internal static class GasCalculationXDataAdapterTests
    {
        private static readonly string SourceHash = new string('b', 64);
        private static int _failed;

        private static void Check(string name, bool passed)
        {
            Console.WriteLine("[XDATA " + (passed ? "OK" : "FAIL") + "] " + name);
            if (!passed) _failed++;
        }

        private static Dictionary<string, string> Fixture()
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GasCalculationXDataAdapter.SchemaKey] = GasCalculationXDataAdapter.SchemaVersion,
                [GasCalculationService.Fields.RoomName] = "XData adapter test room",
                [GasCalculationService.Fields.RoomVolume] = "30",
                [GasCalculationService.Fields.RoomHeight] = "3",
                [GasCalculationService.Fields.OpeningsArea] = "0.015",
                [GasCalculationService.Fields.OpeningsPosition] = "BottomAndTop",
                [GasCalculationService.Fields.MinTemperature] = "20",
                [GasCalculationService.Fields.FireKind] = "ClassAOther",
                [GasCalculationService.Fields.PipeVolume] = "0",
                [GasCalculationService.Fields.Altitude] = "347",
                [GasCalculationService.Fields.K3Mode] = "LowerRow",
                [GasCalculationService.Fields.GasName] = "Test inert gas",
                [GasCalculationService.Fields.GasKind] = "Liquefied",
                [GasCalculationService.Fields.GasReferenceDensity] = "7.28",
                [GasCalculationService.Fields.GasConcentration] = "7.2",
                [GasCalculationService.Fields.GasInjectionTime] = "10",
                [GasCalculationService.Fields.GasVaporDensityCap] = "32",
                [GasCalculationService.Fields.GasLiquidDensity] = "1400",
                [GasCalculationXDataAdapter.CatalogCountKey] = "1",
                [GasCalculationService.Fields.CatalogName(0)] = "Verified test module",
                [GasCalculationService.Fields.CatalogFill(0)] = "50",
                [GasCalculationService.Fields.CatalogResidual(0)] = "0"
            };
        }

        private static void AddEvidence(Dictionary<string, string> data, string field,
            InputEvidenceStatus status, string valueHash)
        {
            string p = "EVIDENCE." + field + ".";
            data[p + "STATUS"] = status.ToString();
            data[p + "VALUE_SHA256"] = valueHash;
            if (status == InputEvidenceStatus.EngineerEntered ||
                status == InputEvidenceStatus.NotApplicableConfirmed)
                data[p + "ACTOR_ID"] = "engineer:test";
            else if (status == InputEvidenceStatus.ImportedVerified ||
                     status == InputEvidenceStatus.ManufacturerDatasheet)
            {
                data[p + "SOURCE_REF"] = "test://controlled-fixture";
                data[p + "SOURCE_SHA256"] = SourceHash;
            }
            else
            {
                data[p + "SOURCE_REF"] = "test://controlled-fixture";
                data[p + "SOURCE_VERSION"] = "fixture-v1";
            }
        }

        private static Dictionary<string, string> FullyEvidencedFixture()
        {
            Dictionary<string, string> data = Fixture();
            GasCalculationXDataAdapterResult first = GasCalculationXDataAdapter.Adapt(data);
            if (first.Request == null) throw new InvalidOperationException("Fixture did not adapt");

            string[] engineerFields =
            {
                GasCalculationService.Fields.RoomName, GasCalculationService.Fields.RoomVolume,
                GasCalculationService.Fields.RoomHeight, GasCalculationService.Fields.OpeningsArea,
                GasCalculationService.Fields.OpeningsPosition, GasCalculationService.Fields.MinTemperature,
                GasCalculationService.Fields.FireKind, GasCalculationService.Fields.PipeVolume
            };
            foreach (string field in engineerFields)
                AddEvidence(data, field, InputEvidenceStatus.EngineerEntered,
                    GasCalculationService.ComputeEvidenceValueSha256(first.Request, field));

            AddEvidence(data, GasCalculationService.Fields.DeltaVolume,
                InputEvidenceStatus.NotApplicableConfirmed,
                GasCalculationService.ComputeEvidenceValueSha256(first.Request, GasCalculationService.Fields.DeltaVolume));

            foreach (string field in new[] { GasCalculationService.Fields.Altitude, GasCalculationService.Fields.K3Mode })
                AddEvidence(data, field, InputEvidenceStatus.ProjectSetting,
                    GasCalculationService.ComputeEvidenceValueSha256(first.Request, field));

            foreach (string field in new[] { GasCalculationService.Fields.GasName,
                GasCalculationService.Fields.GasReferenceDensity, GasCalculationService.Fields.GasVaporDensityCap,
                GasCalculationService.Fields.GasLiquidDensity, GasCalculationService.Fields.CatalogName(0),
                GasCalculationService.Fields.CatalogFill(0), GasCalculationService.Fields.CatalogResidual(0) })
                AddEvidence(data, field, InputEvidenceStatus.ManufacturerDatasheet,
                    GasCalculationService.ComputeEvidenceValueSha256(first.Request, field));

            foreach (string field in new[] { GasCalculationService.Fields.GasKind,
                GasCalculationService.Fields.GasConcentration, GasCalculationService.Fields.GasInjectionTime })
                AddEvidence(data, field, InputEvidenceStatus.NormativeReference,
                    GasCalculationService.ComputeEvidenceValueSha256(first.Request, field));
            return data;
        }

        public static int Run()
        {
            _failed = 0;
            Check("legacy XData without schema is rejected",
                GasCalculationXDataAdapter.Calculate(new Dictionary<string, string>()).IsBlocked);
            Dictionary<string, string> unsupported = Fixture();
            unsupported[GasCalculationXDataAdapter.SchemaKey] = "999";
            Check("unknown schema version is rejected",
                GasCalculationXDataAdapter.Calculate(unsupported).IsBlocked);

            Dictionary<string, string> malformed = Fixture();
            malformed[GasCalculationService.Fields.RoomVolume] = "NaN";
            Check("non-finite numeric XData is rejected before engine",
                GasCalculationXDataAdapter.Calculate(malformed).IsBlocked);

            Dictionary<string, string> badEnum = Fixture();
            badEnum[GasCalculationService.Fields.FireKind] = "ClassZ";
            Check("unknown enum token is rejected",
                GasCalculationXDataAdapter.Calculate(badEnum).IsBlocked);

            Dictionary<string, string> valid = FullyEvidencedFixture();
            GasCalculationOutcome outcome = GasCalculationXDataAdapter.Calculate(valid);
            Check("complete schema-2 XData reaches provenance gate and numerical engine",
                !outcome.IsBlocked && outcome.Calculation != null &&
                !double.IsNaN(outcome.Calculation.Mg) && outcome.Calculation.Mg > 0.0);

            Dictionary<string, string> stale = FullyEvidencedFixture();
            stale[GasCalculationService.Fields.RoomVolume] = "31";
            GasCalculationOutcome staleOutcome = GasCalculationXDataAdapter.Calculate(stale);
            Check("edited XData value with stale evidence fingerprint is blocked",
                staleOutcome.IsBlocked && staleOutcome.BlockingFindings.Exists(
                    x => x.Contains("VALUE_SHA256_MISMATCH", StringComparison.Ordinal)));

            Dictionary<string, string> oversizedCatalog = Fixture();
            oversizedCatalog[GasCalculationXDataAdapter.CatalogCountKey] = "21";
            Check("oversized module catalogue is rejected before combinatorial selection",
                GasCalculationXDataAdapter.Calculate(oversizedCatalog).IsBlocked);

            Dictionary<string, string> missingCatalog = FullyEvidencedFixture();
            missingCatalog.Remove(GasCalculationService.Fields.CatalogFill(0));
            Check("missing module catalogue value is blocked",
                GasCalculationXDataAdapter.Calculate(missingCatalog).IsBlocked);

            Console.WriteLine("[XDATA] scenarios=" + 8 + " failed=" + _failed);
            return _failed;
        }
    }
}
