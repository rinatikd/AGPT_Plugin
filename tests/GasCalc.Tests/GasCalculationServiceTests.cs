using System;
using System.Collections.Generic;
using AGPT.Calc;

namespace AGPT.Calc.Tests
{
    internal static class GasCalculationServiceTests
    {
        private static readonly string Hash = new string('a', 64);
        private static int _failed;

        private static void Check(string label, bool passed)
        {
            Console.WriteLine("[PROVENANCE " + (passed ? "OK" : "FAIL") + "] " + label);
            if (!passed) _failed++;
        }

        private static InputEvidence Entered(string actor = "engineer-001")
        {
            return new InputEvidence
            {
                Status = InputEvidenceStatus.EngineerEntered,
                ActorId = actor
            };
        }

        private static InputEvidence Imported(string source)
        {
            return new InputEvidence
            {
                Status = InputEvidenceStatus.ImportedVerified,
                SourceRef = source,
                SourceSha256 = Hash
            };
        }

        private static InputEvidence ProjectSetting(string source, string version)
        {
            return new InputEvidence
            {
                Status = InputEvidenceStatus.ProjectSetting,
                SourceRef = source,
                SourceVersion = version
            };
        }

        private static InputEvidence Normative(string source, string version)
        {
            return new InputEvidence
            {
                Status = InputEvidenceStatus.NormativeReference,
                SourceRef = source,
                SourceVersion = version
            };
        }

        private static InputEvidence Datasheet(string source)
        {
            return new InputEvidence
            {
                Status = InputEvidenceStatus.ManufacturerDatasheet,
                SourceRef = source,
                SourceSha256 = Hash
            };
        }

        private static GasCalculationRequest ValidRequest()
        {
            var request = new GasCalculationRequest
            {
                Room = new RoomInput
                {
                    Name = "Provenance gate fixture",
                    Volume = 10.0,
                    Height = 3.0,
                    OpeningsArea = 0.005,
                    OpeningsPosition = OpeningsPos.BottomAndTop,
                    TMinC = 20.0,
                    Fire = FireKind.ClassBHeptane,
                    PipeVolume = 0.0,
                    DeltaVolume = null
                },
                Project = new ProjectSettings
                {
                    AltitudeM = 347.0,
                    K3Mode = K3Mode.LowerRow
                },
                Gotv = new GotvSpec
                {
                    Name = "HFC-227ea — verified test profile",
                    Kind = GasKind.Liquefied,
                    Rho0 = 7.28,
                    CnHeptane = 7.2,
                    TauPod = 10.0,
                    VaporDensityCap = 32.0,
                    LiquidDensity = 1400.0
                },
                Catalog = new List<Module>
                {
                    new Module("TEST-MODULE-40", 40.0, 0.2)
                }
            };

            request.Evidence[GasCalculationService.Fields.RoomName] = Entered();
            request.Evidence[GasCalculationService.Fields.RoomVolume] = Entered();
            request.Evidence[GasCalculationService.Fields.RoomHeight] = Entered();
            request.Evidence[GasCalculationService.Fields.OpeningsArea] = Entered();
            request.Evidence[GasCalculationService.Fields.OpeningsPosition] = Entered();
            request.Evidence[GasCalculationService.Fields.MinTemperature] = Entered();
            request.Evidence[GasCalculationService.Fields.FireKind] = Entered();
            request.Evidence[GasCalculationService.Fields.PipeVolume] = Entered();
            request.Evidence[GasCalculationService.Fields.DeltaVolume] = new InputEvidence
            {
                Status = InputEvidenceStatus.NotApplicableConfirmed,
                ActorId = "engineer-001"
            };

            request.Evidence[GasCalculationService.Fields.Altitude] =
                ProjectSetting("project:TEST-001", "rev-1");
            request.Evidence[GasCalculationService.Fields.K3Mode] =
                ProjectSetting("project:TEST-001", "rev-1");

            request.Evidence[GasCalculationService.Fields.GasName] =
                Datasheet("datasheet:TEST-GAS-001");
            request.Evidence[GasCalculationService.Fields.GasKind] =
                Normative("normative:SP-RK-2.02-102-2022", "2022");
            request.Evidence[GasCalculationService.Fields.GasReferenceDensity] =
                Datasheet("datasheet:TEST-GAS-001");
            request.Evidence[GasCalculationService.Fields.GasConcentration] =
                Normative("normative:SP-RK-2.02-102-2022", "2022");
            request.Evidence[GasCalculationService.Fields.GasInjectionTime] =
                Normative("normative:SP-RK-2.02-102-2022", "2022");
            request.Evidence[GasCalculationService.Fields.GasVaporDensityCap] =
                Datasheet("datasheet:TEST-GAS-001");
            request.Evidence[GasCalculationService.Fields.GasLiquidDensity] =
                Datasheet("datasheet:TEST-GAS-001");

            request.Evidence[GasCalculationService.Fields.CatalogName(0)] =
                Datasheet("datasheet:TEST-MODULE-001");
            request.Evidence[GasCalculationService.Fields.CatalogFill(0)] =
                Datasheet("datasheet:TEST-MODULE-001");
            request.Evidence[GasCalculationService.Fields.CatalogResidual(0)] =
                Datasheet("datasheet:TEST-MODULE-001");
            return request;
        }

        public static int Run()
        {
            _failed = 0;

            GasCalculationOutcome good = GasCalculationService.Calculate(ValidRequest());
            Check("complete evidence reaches engine and yields release-ready calculation",
                !good.IsBlocked && good.CanRelease && good.Calculation != null);

            GasCalculationRequest missing = ValidRequest();
            missing.Evidence.Remove(GasCalculationService.Fields.FireKind);
            GasCalculationOutcome missingResult = GasCalculationService.Calculate(missing);
            Check("missing FireKind evidence blocks before calculation",
                missingResult.IsBlocked && missingResult.Calculation == null);

            GasCalculationRequest unconfirmed = ValidRequest();
            unconfirmed.Evidence[GasCalculationService.Fields.MinTemperature] =
                new InputEvidence { Status = InputEvidenceStatus.Unconfirmed, ActorId = "engineer-001" };
            GasCalculationOutcome unconfirmedResult = GasCalculationService.Calculate(unconfirmed);
            Check("unconfirmed temperature cannot be treated as a valid default",
                unconfirmedResult.IsBlocked && unconfirmedResult.Calculation == null);

            GasCalculationRequest badHash = ValidRequest();
            badHash.Evidence[GasCalculationService.Fields.CatalogResidual(0)] =
                new InputEvidence
                {
                    Status = InputEvidenceStatus.ManufacturerDatasheet,
                    SourceRef = "datasheet:TEST-MODULE-001",
                    SourceSha256 = "not-a-sha256"
                };
            GasCalculationOutcome badHashResult = GasCalculationService.Calculate(badHash);
            Check("manufacturer catalogue evidence requires source hash",
                badHashResult.IsBlocked && badHashResult.Calculation == null);

            GasCalculationRequest noPressure = ValidRequest();
            noPressure.Room.PipeVolume = 0.002;
            noPressure.Evidence[GasCalculationService.Fields.PipeVolume] = Entered();
            GasCalculationOutcome noPressureResult = GasCalculationService.Calculate(noPressure);
            Check("positive pipe volume without residual pressure evidence blocks",
                noPressureResult.IsBlocked && noPressureResult.Calculation == null);

            GasCalculationRequest noPressureValue = ValidRequest();
            noPressureValue.Room.PipeVolume = 0.002;
            noPressureValue.Evidence[GasCalculationService.Fields.PipeVolume] = Entered();
            noPressureValue.Evidence[GasCalculationService.Fields.ResidualAbsPressure] =
                Imported("hydraulic:TEST-001");
            GasCalculationOutcome noPressureValueResult = GasCalculationService.Calculate(noPressureValue);
            Check("verified pressure provenance without pressure value still blocks",
                noPressureValueResult.IsBlocked && noPressureValueResult.Calculation == null);

            GasCalculationRequest noCatalogEvidence = ValidRequest();
            noCatalogEvidence.Evidence.Remove(GasCalculationService.Fields.CatalogFill(0));
            GasCalculationOutcome noCatalogResult = GasCalculationService.Calculate(noCatalogEvidence);
            Check("module fill without manufacturer evidence blocks",
                noCatalogResult.IsBlocked && noCatalogResult.Calculation == null);

            GasCalculationRequest deltaMissingDecision = ValidRequest();
            deltaMissingDecision.Room.DeltaVolume = 12.0;
            deltaMissingDecision.Evidence.Remove(GasCalculationService.Fields.DeltaVolume);
            GasCalculationOutcome deltaResult = GasCalculationService.Calculate(deltaMissingDecision);
            Check("specified DeltaVolume requires its own evidence",
                deltaResult.IsBlocked && deltaResult.Calculation == null);

            GasCalculationRequest warning = ValidRequest();
            warning.Room.Fire = FireKind.ClassAOther;
            warning.Room.OpeningsArea = 0.02;
            GasCalculationOutcome warningResult = GasCalculationService.Calculate(warning);
            Check("engine warnings prevent release readiness",
                !warningResult.IsBlocked && warningResult.Calculation != null && !warningResult.CanRelease);

            GasCalculationRequest needsChoice = ValidRequest();
            needsChoice.Room.Volume = 1000.0;
            needsChoice.Room.OpeningsArea = 0.0;
            needsChoice.Catalog = new List<Module> { new Module("TEST-MODULE-40", 40.0, 0.2) };
            GasCalculationOutcome choiceResult = GasCalculationService.Calculate(needsChoice);
            Check("unresolved module selection prevents release readiness",
                !choiceResult.IsBlocked && choiceResult.Calculation != null &&
                choiceResult.Calculation.NeedsEngineerDecision && !choiceResult.CanRelease);

            return _failed;
        }
    }
}
