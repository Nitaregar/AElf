using System;
using System.Collections.Generic;
using System.Linq;
using AElf.Standards.ACS1;
using AElf.Standards.ACS10;
using AElf.CSharp.Core;
using AElf.Sdk.CSharp;
using AElf.Types;
using Google.Protobuf.WellKnownTypes;

namespace AElf.Contracts.MultiToken
{
    public partial class TokenContract
    {
        /// <summary>
        /// Related transactions will be generated by acs1 pre-plugin service,
        /// and will be executed before the origin transaction.
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public override ChargeTransactionFeesOutput ChargeTransactionFees(ChargeTransactionFeesInput input)
        {
            AssertTransactionGeneratedByPlugin();
            Assert(input.MethodName != null && input.ContractAddress != null, "Invalid charge transaction fees input.");

            // Primary token not created yet.
            if (State.ChainPrimaryTokenSymbol.Value == null)
            {
                return new ChargeTransactionFeesOutput {Success = true};
            }

            // Record tx fee bill during current charging process.
            var bill = new TransactionFeeBill();

            var fromAddress = Context.Sender;
            var methodFees = Context.Call<MethodFees>(input.ContractAddress, nameof(GetMethodFee),
                new StringValue {Value = input.MethodName});
            var successToChargeBaseFee = true;
            if (methodFees != null && methodFees.Fees.Any())
            {
                // If base fee is set before, charge base fee.
                successToChargeBaseFee = ChargeBaseFee(GetBaseFeeDictionary(methodFees), ref bill);
            }

            var successToChargeSizeFee = true;
            if (methodFees != null && !methodFees.IsSizeFeeFree)
            {
                // If IsSizeFeeFree == true, do not charge size fee.
                successToChargeSizeFee = ChargeSizeFee(input, ref bill);
            }

            SetOrRefreshMethodFeeFreeAllowances(fromAddress);
            var freeAllowances = CalculateMethodFeeFreeAllowances(fromAddress);

            // Update balances.
            foreach (var (symbol, amount) in bill.FeesMap)
            {
                var actualAmount = amount;
                var freeAllowance = freeAllowances?.Value.FirstOrDefault(a => a.Symbol == symbol);
                if (freeAllowance != null)
                {
                    // Consume free allowance.
                    var consumingFreeAllowance = Math.Min(amount, freeAllowance.Amount);
                    freeAllowance.Amount = freeAllowance.Amount.Sub(consumingFreeAllowance);
                    actualAmount = amount.Sub(consumingFreeAllowance);
                    if (actualAmount == 0)
                    {
                        // Seek for charging next token if the free allowance is able to cover current token.
                        continue;
                    }
                }

                ModifyBalance(fromAddress, symbol, -actualAmount);
                Context.Fire(new TransactionFeeCharged
                {
                    Symbol = symbol,
                    Amount = actualAmount
                });
                if (actualAmount == 0)
                {
                    Context.LogDebug(() => $"Maybe incorrect charged tx fee of {symbol}: it's 0.");
                }
            }

            State.MethodFeeFreeAllowancesMap[fromAddress] = freeAllowances;
            var chargingResult = successToChargeBaseFee && successToChargeSizeFee;
            var chargingOutput = new ChargeTransactionFeesOutput {Success = chargingResult};
            if (!chargingResult)
                chargingOutput.ChargingInformation = "Transaction fee not enough.";
            return chargingOutput;
        }

        private void SetOrRefreshMethodFeeFreeAllowances(Address address)
        {
            var config = State.MethodFeeFreeAllowancesConfig.Value;
            if (State.Balances[address][Context.Variables.NativeSymbol] < config.Threshold)
            {
                return;
            }

            var lastRefreshTime = State.MethodFeeFreeAllowancesLastRefreshTimeMap[address];
            if (lastRefreshTime != null && config.Threshold > (Context.CurrentBlockTime - lastRefreshTime).Seconds)
            {
                return;
            }

            State.MethodFeeFreeAllowancesLastRefreshTimeMap[address] = Context.CurrentBlockTime;
            State.MethodFeeFreeAllowancesMap[address] = config.FreeAllowances;
        }

        private Dictionary<string, long> GetBaseFeeDictionary(MethodFees methodFees)
        {
            var dict = new Dictionary<string, long>();
            foreach (var methodFee in methodFees.Fees)
            {
                if (dict.ContainsKey(methodFee.Symbol))
                {
                    dict[methodFee.Symbol] = dict[methodFee.Symbol].Add(methodFee.BasicFee);
                }
                else
                {
                    dict[methodFee.Symbol] = methodFee.BasicFee;
                }
            }

            return dict;
        }

        private bool ChargeBaseFee(Dictionary<string, long> methodFeeMap, ref TransactionFeeBill bill)
        {
            if (!ChargeFirstSufficientToken(methodFeeMap, out var symbolToChargeBaseFee,
                out var amountToChargeBaseFee, out var existingBalance))
            {
                Context.LogDebug(() => "Failed to charge first sufficient token.");
                if (symbolToChargeBaseFee != null)
                {
                    bill.FeesMap.Add(symbolToChargeBaseFee, existingBalance);
                } // If symbol == null, then charge nothing in base fee part.

                return false;
            }

            bill.FeesMap.Add(symbolToChargeBaseFee, amountToChargeBaseFee);

            return true;
        }

        private bool ChargeSizeFee(ChargeTransactionFeesInput input, ref TransactionFeeBill bill)
        {
            string symbolChargedForBaseFee = null;
            var amountChargedForBaseFee = 0L;
            var symbolToPayTxFee = State.ChainPrimaryTokenSymbol.Value;
            if (bill.FeesMap.Any())
            {
                symbolChargedForBaseFee = bill.FeesMap.First().Key;
                amountChargedForBaseFee = bill.FeesMap.First().Value;
            }

            var availableBalance = symbolChargedForBaseFee == symbolToPayTxFee
                // Available balance need to deduct amountChargedForBaseFee
                ? GetBalance(Context.Sender, symbolToPayTxFee).Sub(amountChargedForBaseFee)
                : GetBalance(Context.Sender, symbolToPayTxFee);
            var txSizeFeeAmount = input.TransactionSizeFee;

            if (input.SymbolsToPayTxSizeFee.Any())
            {
                var allSymbolToTxFee = input.SymbolsToPayTxSizeFee;
                var availableSymbol = allSymbolToTxFee.FirstOrDefault(x =>
                                          GetBalanceCalculatedBaseOnPrimaryToken(x, symbolChargedForBaseFee,
                                              amountChargedForBaseFee) >= txSizeFeeAmount) ??
                                      allSymbolToTxFee.FirstOrDefault(x =>
                                          GetBalanceCalculatedBaseOnPrimaryToken(x, symbolChargedForBaseFee,
                                              amountChargedForBaseFee) > 0);
                if (availableSymbol != null && availableSymbol.TokenSymbol != symbolToPayTxFee)
                {
                    symbolToPayTxFee = availableSymbol.TokenSymbol;
                    txSizeFeeAmount = txSizeFeeAmount.Mul(availableSymbol.AddedTokenWeight)
                        .Div(availableSymbol.BaseTokenWeight);
                    availableBalance = symbolChargedForBaseFee == symbolToPayTxFee
                        ? GetBalance(Context.Sender, symbolToPayTxFee).Sub(amountChargedForBaseFee)
                        : GetBalance(Context.Sender, symbolToPayTxFee);
                }
            }

            var chargeAmount = availableBalance > txSizeFeeAmount
                ? txSizeFeeAmount
                : availableBalance;

            if (symbolToPayTxFee == null) return availableBalance >= txSizeFeeAmount;

            if (symbolChargedForBaseFee == symbolToPayTxFee)
            {
                bill.FeesMap[symbolToPayTxFee] =
                    bill.FeesMap[symbolToPayTxFee].Add(chargeAmount);
            }
            else
            {
                bill.FeesMap.Add(symbolToPayTxFee, chargeAmount);
            }

            return availableBalance >= txSizeFeeAmount;
        }

        public override Empty ChargeResourceToken(ChargeResourceTokenInput input)
        {
            AssertTransactionGeneratedByPlugin();
            Context.LogDebug(() => $"Start executing ChargeResourceToken.{input}");
            if (input.Equals(new ChargeResourceTokenInput()))
            {
                return new Empty();
            }

            var bill = new TransactionFeeBill();
            foreach (var pair in input.CostDic)
            {
                Context.LogDebug(() => $"Charging {pair.Value} {pair.Key} tokens.");
                var existingBalance = GetBalance(Context.Sender, pair.Key);
                Assert(existingBalance >= pair.Value,
                    $"Insufficient resource of {pair.Key}. Need balance: {pair.Value}; Current balance: {existingBalance}.");
                bill.FeesMap.Add(pair.Key, pair.Value);
            }

            foreach (var pair in bill.FeesMap)
            {
                Context.Fire(new ResourceTokenCharged
                {
                    Symbol = pair.Key,
                    Amount = pair.Value,
                    ContractAddress = Context.Sender
                });
                if (pair.Value == 0)
                {
                    Context.LogDebug(() => $"Maybe incorrect charged resource fee of {pair.Key}: it's 0.");
                }
            }

            return new Empty();
        }

        public override Empty CheckResourceToken(Empty input)
        {
            AssertTransactionGeneratedByPlugin();
            foreach (var symbol in Context.Variables.GetStringArray(TokenContractConstants.PayTxFeeSymbolListName))
            {
                var balance = GetBalance(Context.Sender, symbol);
                var owningBalance = State.OwningResourceToken[Context.Sender][symbol];
                Assert(balance > owningBalance,
                    $"Contract balance of {symbol} token is not enough. Owning {owningBalance}.");
            }

            return new Empty();
        }

        public override Empty SetSymbolsToPayTxSizeFee(SymbolListToPayTxSizeFee input)
        {
            AssertControllerForSymbolToPayTxSizeFee();
            if (input == null)
                throw new AssertionException("invalid input");
            var isPrimaryTokenExist = false;
            var symbolList = new List<string>();
            var primaryTokenSymbol = GetPrimaryTokenSymbol(new Empty());
            var primaryTokenInfo = State.TokenInfos[primaryTokenSymbol.Value];
            Assert(!string.IsNullOrEmpty(primaryTokenSymbol.Value), "primary token does not exist");
            foreach (var tokenWeightInfo in input.SymbolsToPayTxSizeFee)
            {
                if (tokenWeightInfo.TokenSymbol == primaryTokenSymbol.Value)
                {
                    isPrimaryTokenExist = true;
                    Assert(tokenWeightInfo.AddedTokenWeight == 1 && tokenWeightInfo.BaseTokenWeight == 1,
                        $"symbol:{tokenWeightInfo.TokenSymbol} weight should be 1");
                }

                Assert(tokenWeightInfo.AddedTokenWeight > 0 && tokenWeightInfo.BaseTokenWeight > 0,
                    $"symbol:{tokenWeightInfo.TokenSymbol} weight should be greater than 0");
                Assert(!symbolList.Contains(tokenWeightInfo.TokenSymbol),
                    $"symbol:{tokenWeightInfo.TokenSymbol} repeat");
                AssertSymbolToPayTxFeeIsValid(tokenWeightInfo.TokenSymbol, out var addedTokenTotalSupply);
                symbolList.Add(tokenWeightInfo.TokenSymbol);
            }

            Assert(isPrimaryTokenExist, $"primary token:{primaryTokenSymbol.Value} not included");
            State.SymbolListToPayTxSizeFee.Value = input;
            Context.Fire(new ExtraTokenListModified
            {
                SymbolListToPayTxSizeFee = input
            });
            return new Empty();
        }

        /// <summary>
        /// Example 1:
        /// symbolToAmountMap: {{"ELF", 10}, {"TSA", 1}, {"TSB", 2}}
        ///
        /// [Charge successful]
        /// Sender's balance:
        /// ELF - 9
        /// TSA - 0
        /// TSB - 3
        /// Then charge 2 TSBs.
        ///
        /// [Charge failed]
        /// Sender's balance:
        /// ELF - 9
        /// TSA - 0
        /// TSB - 1
        /// Then charge 9 ELFs
        ///
        /// Example 2:
        /// symbolToAmountMap: {{"TSA", 1}, {"TSB", 2}}
        /// which means the charging token symbol list doesn't contain the native symbol.
        ///
        /// [Charge successful]
        /// Sender's balance:
        /// ELF - 1
        /// TSA - 2
        /// TSB - 2
        /// Then charge 1 TSA
        ///
        /// [Charge failed]
        /// Sender's balance:
        /// ELF - 1
        /// TSA - 0
        /// TSB - 1
        /// Then charge 1 TSB
        ///
        /// [Charge failed]
        /// Sender's balance:
        /// ELF - 1000000000
        /// TSA - 0
        /// TSB - 0
        /// Then charge nothing.
        /// (Contract developer should be suggested to implement acs5 to check certain balance or allowance of sender.)
        /// </summary>
        /// <param name="symbolToAmountMap"></param>
        /// <param name="symbol"></param>
        /// <param name="amount"></param>
        /// <param name="existingBalance"></param>
        /// <returns></returns>
        private bool ChargeFirstSufficientToken(Dictionary<string, long> symbolToAmountMap, out string symbol,
            out long amount, out long existingBalance)
        {
            symbol = null;
            amount = 0L;
            existingBalance = 0L;
            var fromAddress = Context.Sender;
            string symbolOfValidBalance = null;

            // Traverse available token symbols, check balance one by one
            // until there's balance of one certain token is enough to pay the fee.
            foreach (var symbolToAmount in symbolToAmountMap)
            {
                existingBalance = GetBalance(fromAddress, symbolToAmount.Key);
                symbol = symbolToAmount.Key;
                amount = symbolToAmount.Value;

                if (existingBalance > 0)
                {
                    symbolOfValidBalance = symbol;
                }

                if (existingBalance >= amount) break;
            }

            if (existingBalance >= amount) return true;

            var primaryTokenSymbol = GetPrimaryTokenSymbol(new Empty()).Value;
            if (symbolToAmountMap.Keys.Contains(primaryTokenSymbol))
            {
                symbol = primaryTokenSymbol;
                existingBalance = GetBalance(fromAddress, primaryTokenSymbol);
            }
            else
            {
                symbol = symbolOfValidBalance;
                if (symbol != null)
                {
                    existingBalance = GetBalance(fromAddress, symbolOfValidBalance);
                }
            }

            return false;
        }

        public override Empty ClaimTransactionFees(TotalTransactionFeesMap input)
        {
            AssertSenderIsCurrentMiner();
            var claimTransactionExecuteHeight = State.ClaimTransactionFeeExecuteHeight.Value;

            Assert(claimTransactionExecuteHeight < Context.CurrentHeight,
                $"This method already executed in height {State.ClaimTransactionFeeExecuteHeight.Value}");
            State.ClaimTransactionFeeExecuteHeight.Value = Context.CurrentHeight;
            Context.LogDebug(() => $"Claim transaction fee. {input}");
            State.LatestTotalTransactionFeesMapHash.Value = HashHelper.ComputeFrom(input);
            foreach (var bill in input.Value)
            {
                var symbol = bill.Key;
                var amount = bill.Value;
                ModifyBalance(Context.Self, symbol, amount);
                TransferTransactionFeesToFeeReceiver(symbol, amount);
            }

            Context.LogDebug(() => "Finish claim transaction fee.");

            return new Empty();
        }

        private void AssertSenderIsCurrentMiner()
        {
            if (State.ConsensusContract.Value == null)
            {
                State.ConsensusContract.Value =
                    Context.GetContractAddressByName(SmartContractConstants.ConsensusContractSystemName);
            }

            Assert(State.ConsensusContract.IsCurrentMiner.Call(Context.Sender).Value, "No permission.");
        }

        public override Hash GetLatestTotalTransactionFeesMapHash(Empty input)
        {
            return State.LatestTotalTransactionFeesMapHash.Value;
        }

        public override Empty DonateResourceToken(TotalResourceTokensMaps input)
        {
            AssertSenderIsCurrentMiner();
            var donateResourceTokenExecuteHeight = State.DonateResourceTokenExecuteHeight.Value;
            if (donateResourceTokenExecuteHeight == 0)
            {
                donateResourceTokenExecuteHeight = Context.CurrentHeight;
            }

            Assert(donateResourceTokenExecuteHeight == Context.CurrentHeight,
                $"This method already executed in height {State.DonateResourceTokenExecuteHeight.Value}");
            State.DonateResourceTokenExecuteHeight.Value = donateResourceTokenExecuteHeight.Add(1);
            Context.LogDebug(() => $"Start donate resource token. {input}");
            State.LatestTotalResourceTokensMapsHash.Value = HashHelper.ComputeFrom(input);
            Context.LogDebug(() =>
                $"Now LatestTotalResourceTokensMapsHash is {State.LatestTotalResourceTokensMapsHash.Value}");

            var isMainChain = true;
            if (State.DividendPoolContract.Value == null)
            {
                var treasuryContractAddress =
                    Context.GetContractAddressByName(SmartContractConstants.TreasuryContractSystemName);
                if (treasuryContractAddress == null)
                {
                    isMainChain = false;
                }
                else
                {
                    State.DividendPoolContract.Value = treasuryContractAddress;
                }
            }

            PayResourceTokens(input, isMainChain);

            if (!isMainChain)
            {
                PayRental();
            }

            return new Empty();
        }

        public override Hash GetLatestTotalResourceTokensMapsHash(Empty input)
        {
            return State.LatestTotalResourceTokensMapsHash.Value;
        }

        private void PayResourceTokens(TotalResourceTokensMaps billMaps, bool isMainChain)
        {
            foreach (var bill in billMaps.Value)
            {
                foreach (var feeMap in bill.TokensMap.Value)
                {
                    var symbol = feeMap.Key;
                    var amount = feeMap.Value;
                    // Check balance in case of insufficient balance.
                    var existingBalance = GetBalance(bill.ContractAddress, symbol);
                    if (amount > existingBalance)
                    {
                        var owned = amount.Sub(existingBalance);
                        var currentOwning = State.OwningResourceToken[bill.ContractAddress][symbol].Add(owned);
                        State.OwningResourceToken[bill.ContractAddress][symbol] = currentOwning;
                        Context.Fire(new ResourceTokenOwned
                        {
                            Symbol = symbol,
                            Amount = currentOwning,
                            ContractAddress = bill.ContractAddress
                        });
                        amount = existingBalance;
                    }

                    if (amount > 0)
                    {
                        ModifyBalance(bill.ContractAddress, symbol, -amount);
                        if (isMainChain)
                        {
                            Context.LogDebug(() => $"Adding {amount} of {symbol}s to dividend pool.");
                            // Main Chain.
                            ModifyBalance(Context.Self, symbol, amount);
                            State.DividendPoolContract.Donate.Send(new DonateInput
                            {
                                Symbol = symbol,
                                Amount = amount
                            });
                        }
                        else
                        {
                            Context.LogDebug(() => $"Adding {amount} of {symbol}s to consensus address account.");
                            // Side Chain
                            var consensusContractAddress =
                                Context.GetContractAddressByName(SmartContractConstants.ConsensusContractSystemName);
                            ModifyBalance(consensusContractAddress, symbol, amount);
                        }
                    }
                }
            }
        }

        private void PayRental()
        {
            var creator = State.SideChainCreator.Value;
            if (creator == null) return;
            if (State.LastPayRentTime.Value == null)
            {
                // Initial LastPayRentTime first calling DonateResourceToken.
                State.LastPayRentTime.Value = Context.CurrentBlockTime;
                return;
            }

            // We need minutes.
            var duration = (Context.CurrentBlockTime - State.LastPayRentTime.Value).Seconds.Div(60);
            if (duration == 0)
            {
                return;
            }

            // Update LastPayRentTime if it is ready to charge rental.
            State.LastPayRentTime.Value += new Duration {Seconds = duration.Mul(60)};

            foreach (var symbol in Context.Variables.GetStringArray(TokenContractConstants.PayRentalSymbolListName))
            {
                var donates = 0L;

                var availableBalance = GetBalance(creator, symbol);

                // Try to update owning rental.
                var owningRental = State.OwningRental[symbol];
                if (owningRental > 0)
                {
                    // If Creator own this symbol and current balance can cover the debt, pay the debt at first.
                    if (availableBalance > owningRental)
                    {
                        donates = owningRental;
                        // Need to update available balance,
                        // cause existing balance not necessary equals to available balance.
                        availableBalance = availableBalance.Sub(owningRental);
                        State.OwningRental[symbol] = 0;
                    }
                }

                var rental = duration.Mul(State.ResourceAmount[symbol]).Mul(State.Rental[symbol]);
                if (availableBalance >= rental) // Success
                {
                    donates = donates.Add(rental);
                    ModifyBalance(creator, symbol, -donates);
                }
                else // Fail
                {
                    // Donate all existing balance. Directly reset the donates.
                    donates = GetBalance(creator, symbol);
                    State.Balances[creator][symbol] = 0;

                    // Update owning rental to record a new debt.
                    var own = rental.Sub(availableBalance);
                    State.OwningRental[symbol] = State.OwningRental[symbol].Add(own);

                    Context.Fire(new RentalAccountBalanceInsufficient
                    {
                        Symbol = symbol,
                        Amount = own
                    });
                }

                // Side Chain donates.
                var consensusContractAddress =
                    Context.GetContractAddressByName(SmartContractConstants.ConsensusContractSystemName);
                ModifyBalance(consensusContractAddress, symbol, donates);

                Context.Fire(new RentalCharged()
                {
                    Symbol = symbol,
                    Amount = donates
                });
            }
        }

        public override Empty UpdateRental(UpdateRentalInput input)
        {
            AssertControllerForSideChainRental();
            foreach (var pair in input.Rental)
            {
                Assert(
                    Context.Variables.GetStringArray(TokenContractConstants.PayRentalSymbolListName).Contains(pair.Key),
                    "Invalid symbol.");
                Assert(pair.Value >= 0, "Invalid amount.");
                State.Rental[pair.Key] = pair.Value;
            }

            return new Empty();
        }

        public override Empty UpdateRentedResources(UpdateRentedResourcesInput input)
        {
            AssertControllerForSideChainRental();
            foreach (var pair in input.ResourceAmount)
            {
                Assert(
                    Context.Variables.GetStringArray(TokenContractConstants.PayRentalSymbolListName).Contains(pair.Key),
                    "Invalid symbol.");
                Assert(pair.Value >= 0, "Invalid amount.");
                State.ResourceAmount[pair.Key] = pair.Value;
            }

            return new Empty();
        }

        private void SetSideChainCreator(Address input)
        {
            Assert(State.SideChainCreator.Value == null, "Creator already set.");
            if (State.ParliamentContract.Value == null)
            {
                State.ParliamentContract.Value =
                    Context.GetContractAddressByName(SmartContractConstants.ParliamentContractSystemName);
            }

            Assert(Context.Sender == Context.GetZeroSmartContractAddress() ||
                   Context.Sender == State.ParliamentContract.GetDefaultOrganizationAddress.Call(new Empty()),
                "No permission.");
            State.SideChainCreator.Value = input;
        }

        /// <summary>
        /// Burn 10% of tx fees.
        /// If Side Chain didn't set FeeReceiver, burn all.
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="totalAmount"></param>
        private void TransferTransactionFeesToFeeReceiver(string symbol, long totalAmount)
        {
            Context.LogDebug(() => "Transfer transaction fee to receiver.");

            if (totalAmount <= 0) return;

            var tokenInfo = State.TokenInfos[symbol];
            if (!tokenInfo.IsBurnable)
            {
                return;
            }

            var burnAmount = totalAmount.Div(10);
            if (burnAmount > 0)
                Context.SendInline(Context.Self, nameof(Burn), new BurnInput
                {
                    Symbol = symbol,
                    Amount = burnAmount
                });

            var transferAmount = totalAmount.Sub(burnAmount);
            if (transferAmount == 0)
                return;
            var treasuryContractAddress =
                Context.GetContractAddressByName(SmartContractConstants.TreasuryContractSystemName);
            var isMainChain = treasuryContractAddress != null;
            if (isMainChain)
            {
                // Main chain would donate tx fees to dividend pool.
                if (State.DividendPoolContract.Value == null)
                    State.DividendPoolContract.Value = treasuryContractAddress;
                State.Allowances[Context.Self][State.DividendPoolContract.Value][symbol] =
                    State.Allowances[Context.Self][State.DividendPoolContract.Value][symbol].Add(transferAmount);
                State.DividendPoolContract.Donate.Send(new DonateInput
                {
                    Symbol = symbol,
                    Amount = transferAmount
                });
            }
            else
            {
                if (State.FeeReceiver.Value != null)
                {
                    Context.SendInline(Context.Self, nameof(Transfer), new TransferInput
                    {
                        To = State.FeeReceiver.Value,
                        Symbol = symbol,
                        Amount = transferAmount,
                    });
                }
                else
                {
                    // Burn all!
                    Context.SendInline(Context.Self, nameof(Burn), new BurnInput
                    {
                        Symbol = symbol,
                        Amount = transferAmount
                    });
                }
            }
        }

        public override Empty SetFeeReceiver(Address input)
        {
            Assert(State.SideChainCreator.Value == Context.Sender, "No permission.");
            State.FeeReceiver.Value = input;
            return new Empty();
        }

        public override Address GetFeeReceiver(Empty input)
        {
            return State.FeeReceiver.Value;
        }

        public override Empty ConfigMethodFeeFreeAllowances(MethodFeeFreeAllowancesConfig input)
        {
            AssertSenderAddressWith(GetDefaultParliamentController().OwnerAddress);
            State.MethodFeeFreeAllowancesConfig.Value = input;
            return new Empty();
        }

        public override MethodFeeFreeAllowancesConfig GetMethodFeeFreeAllowancesConfig(Empty input)
        {
            return State.MethodFeeFreeAllowancesConfig.Value;
        }

        public override MethodFeeFreeAllowances GetMethodFeeFreeAllowances(Address input)
        {
            return CalculateMethodFeeFreeAllowances(input);
        }

        private MethodFeeFreeAllowances CalculateMethodFeeFreeAllowances(Address input)
        {
            var freeAllowances = State.MethodFeeFreeAllowancesMap[input];
            var freeAllowancesConfig = State.MethodFeeFreeAllowancesConfig.Value;
            var lastRefreshTime = State.MethodFeeFreeAllowancesLastRefreshTimeMap[input];

            if (freeAllowances == null)
            {
                if (State.Balances[input][Context.Variables.NativeSymbol] >= freeAllowancesConfig.Threshold)
                {
                    return freeAllowancesConfig.FreeAllowances;
                }
            }

            if (lastRefreshTime == null)
            {
                return freeAllowances;
            }

            return (Context.CurrentBlockTime - lastRefreshTime).Seconds > freeAllowancesConfig.RefreshSeconds
                ? freeAllowancesConfig.FreeAllowances
                : freeAllowances;
        }

        private long GetBalanceCalculatedBaseOnPrimaryToken(SymbolToPayTxSizeFee tokenInfo, string baseSymbol,
            long cost)
        {
            var availableBalance = GetBalance(Context.Sender, tokenInfo.TokenSymbol);
            if (tokenInfo.TokenSymbol == baseSymbol)
                availableBalance -= cost;
            return availableBalance.Mul(tokenInfo.BaseTokenWeight)
                .Div(tokenInfo.AddedTokenWeight);
        }

        private void AssertSymbolToPayTxFeeIsValid(string tokenSymbol, out long totalSupply)
        {
            var tokenInfo = State.TokenInfos[tokenSymbol];
            if (tokenInfo == null)
            {
                throw new AssertionException($"Token is not found. {tokenSymbol}");
            }

            Assert(IsTokenAvailableForMethodFee(tokenSymbol), $"Token {tokenSymbol} cannot set as method fee.");
            totalSupply = tokenInfo.TotalSupply;
        }

        private void AssertTransactionGeneratedByPlugin()
        {
            Assert(Context.TransactionId != Context.OriginTransactionId,
                "This method can only be executed in plugin tx.");
        }
    }
}