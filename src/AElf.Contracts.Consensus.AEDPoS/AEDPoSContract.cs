﻿using System.Linq;
using AElf.Contracts.Election;
using AElf.Kernel;
using AElf.Sdk.CSharp;
using Google.Protobuf.WellKnownTypes;

namespace AElf.Contracts.Consensus.AEDPoS
{
    public partial class AEDPoSContract : AEDPoSContractImplContainer.AEDPoSContractImplBase
    {
        #region Initial

        public override Empty InitialAElfConsensusContract(InitialAElfConsensusContractInput input)
        {
            Assert(!State.Initialized.Value, "Already initialized.");

            State.TimeEachTerm.Value = input.IsSideChain || input.IsTermStayOne
                ? int.MaxValue
                : input.TimeEachTerm;

            Context.LogDebug(() => $"Time each term: {State.TimeEachTerm.Value} seconds.");

            State.BasicContractZero.Value = Context.GetZeroSmartContractAddress();

            if (input.IsTermStayOne || input.IsSideChain)
            {
                return new Empty();
            }

            State.ElectionContractSystemName.Value = input.ElectionContractSystemName;

            State.ElectionContract.Value =
                State.BasicContractZero.GetContractAddressByName.Call(input.ElectionContractSystemName);

            State.ElectionContract.RegisterElectionVotingEvent.Send(new Empty());

            State.ElectionContract.CreateTreasury.Send(new Empty());

            State.ElectionContract.RegisterToTreasury.Send(new Empty());

            return new Empty();
        }

        #endregion

        #region FirstRound

        public override Empty FirstRound(Round input)
        {
            Assert(Context.Sender == Context.GetZeroSmartContractAddress(), "Sender must be contract zero.");
            Assert(input.RoundNumber == 1, "Invalid round number.");
            Assert(input.RealTimeMinersInformation.Any(), "No miner in input data.");

            State.CurrentTermNumber.Value = 1;
            State.CurrentRoundNumber.Value = 1;
            State.FirstRoundNumberOfEachTerm[1] = 1L;
            SetBlockchainStartTimestamp(input.GetStartTime().ToTimestamp());
            State.MiningInterval.Value = input.GetMiningInterval();

            if (State.ElectionContract.Value != null)
            {
                State.ElectionContract.ConfigElectionContract.Send(new ConfigElectionContractInput
                {
                    MinerList = {input.RealTimeMinersInformation.Keys},
                    TimeEachTerm = State.TimeEachTerm.Value
                });
            }

            var minerList = new MinerList
                {PublicKeys = {input.RealTimeMinersInformation.Keys.Select(k => k.ToByteString())}};
            SetMinerListOfCurrentTerm(minerList);

            Assert(TryToAddRoundInformation(input), "Failed to add round information.");
            return new Empty();
        }

        #endregion

        #region UpdateValue

        public override Empty UpdateValue(UpdateValueInput input)
        {
            Assert(TryToGetCurrentRoundInformation(out var round), "Round information not found.");
            Assert(input.RoundId == round.RoundId, "Round Id not matched.");

            var publicKey = Context.RecoverPublicKey().ToHex();

            var minerInRound = round.RealTimeMinersInformation[publicKey];
            minerInRound.ActualMiningTimes.Add(input.ActualMiningTime);
            minerInRound.ProducedBlocks = input.ProducedBlocks;
            var producedTinyBlocks = round.RealTimeMinersInformation[publicKey].ProducedTinyBlocks;
            minerInRound.ProducedTinyBlocks = producedTinyBlocks.Add(1);

            minerInRound.Signature = input.Signature;
            minerInRound.OutValue = input.OutValue;
            minerInRound.SupposedOrderOfNextRound = input.SupposedOrderOfNextRound;
            minerInRound.FinalOrderOfNextRound = input.SupposedOrderOfNextRound;

            minerInRound.EncryptedInValues.Add(input.EncryptedInValues);
            foreach (var decryptedPreviousInValue in input.DecryptedPreviousInValues)
            {
                round.RealTimeMinersInformation[decryptedPreviousInValue.Key].DecryptedPreviousInValues
                    .Add(publicKey, decryptedPreviousInValue.Value);
            }

            foreach (var previousInValue in input.MinersPreviousInValues)
            {
                if (previousInValue.Key == publicKey)
                {
                    continue;
                }

                var filledValue = round.RealTimeMinersInformation[previousInValue.Key].PreviousInValue;
                if (filledValue != null && filledValue != previousInValue.Value)
                {
                    Context.LogDebug(() => $"Something wrong happened to previous in value of {previousInValue.Key}.");
                    State.ElectionContract.UpdateCandidateInformation.Send(new UpdateCandidateInformationInput
                    {
                        PublicKey = publicKey,
                        IsEvilNode = true
                    });
                }

                round.RealTimeMinersInformation[previousInValue.Key].PreviousInValue = previousInValue.Value;
            }

            foreach (var tuneOrder in input.TuneOrderInformation)
            {
                round.RealTimeMinersInformation[tuneOrder.Key].FinalOrderOfNextRound = tuneOrder.Value;
            }

            // For first round of each term, no one need to publish in value.
            if (input.PreviousInValue != Hash.Empty)
            {
                minerInRound.PreviousInValue = input.PreviousInValue;
            }

            Assert(TryToUpdateRoundInformation(round), "Failed to update round information.");

            TryToFindLastIrreversibleBlock();

            return new Empty();
        }

        #endregion

        #region UpdateTinyBlockInformation

        public override Empty UpdateTinyBlockInformation(TinyBlockInput input)
        {
            Assert(TryToGetCurrentRoundInformation(out var round), "Round information not found.");
            Assert(input.RoundId == round.RoundId, "Round Id not matched.");

            var publicKey = Context.RecoverPublicKey().ToHex();

            round.RealTimeMinersInformation[publicKey].ActualMiningTimes.Add(input.ActualMiningTime);
            round.RealTimeMinersInformation[publicKey].ProducedBlocks = input.ProducedBlocks;
            var producedTinyBlocks = round.RealTimeMinersInformation[publicKey].ProducedTinyBlocks;
            round.RealTimeMinersInformation[publicKey].ProducedTinyBlocks = producedTinyBlocks.Add(1);

            Assert(TryToUpdateRoundInformation(round), "Failed to update round information.");

            return new Empty();
        }

        #endregion

        #region NextRound

        public override Empty NextRound(Round input)
        {
            if (TryToGetRoundNumber(out var currentRoundNumber))
            {
                Assert(currentRoundNumber < input.RoundNumber, "Incorrect round number for next round.");
            }

            if (currentRoundNumber == 1)
            {
                var actualBlockchainStartTimestamp = input.GetStartTime().ToTimestamp();
                SetBlockchainStartTimestamp(actualBlockchainStartTimestamp);
            }
            else
            {
                var minersCount = GetMinersCount();
                if (minersCount != 0 && State.ElectionContract.Value != null)
                {
                    State.ElectionContract.UpdateMinersCount.Send(new UpdateMinersCountInput
                    {
                        MinersCount = minersCount
                    });
                }
            }

            Assert(TryToGetCurrentRoundInformation(out _), "Failed to get current round information.");
            Assert(TryToAddRoundInformation(input), "Failed to add round information.");
            Assert(TryToUpdateRoundNumber(input.RoundNumber), "Failed to update round number.");
            TryToFindLastIrreversibleBlock();

            return new Empty();
        }

        #endregion

        #region UpdateConsensusInformation

        public override Empty UpdateConsensusInformation(ConsensusInformation input)
        {
            Assert(State.ElectionContract.Value == null, "Only side chain can update consensus information.");
            // For now we just extract the miner list from main chain consensus information, then update miners list.
            if (input == null || input.Bytes.IsEmpty)
                return new Empty();
            var consensusInformation = AElfConsensusHeaderInformation.Parser.ParseFrom(input.Bytes);

            // check round number of shared consensus, not term number
            if (consensusInformation.Round.RoundNumber <= State.MainChainRoundNumber.Value)
                return new Empty();
            Context.LogDebug(() => $"Shared miner list of round {consensusInformation.Round.RoundNumber}");
            var minersKeys = consensusInformation.Round.RealTimeMinersInformation.Keys;
            State.MainChainRoundNumber.Value = consensusInformation.Round.RoundNumber;
            State.MainChainCurrentMiners.Value = new MinerList
            {
                PublicKeys = {minersKeys.Select(k => k.ToByteString())}
            };
            return new Empty();
        }

        #endregion
    }
}