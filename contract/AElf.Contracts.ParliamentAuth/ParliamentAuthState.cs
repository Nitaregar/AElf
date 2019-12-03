using Acs0;
using Acs1;
using AElf.Contracts.Consensus.AEDPoS;
using AElf.Sdk.CSharp.State;
using AElf.Types;

namespace AElf.Contracts.ParliamentAuth
{
    public class ParliamentAuthState : ContractState
    {
        public MappedState<Address, Organization> Organisations { get; set; }

        public BoolState Initialized { get; set; }
        
        public SingletonState<Address> DefaultOrganizationAddress { get; set; }
        
        public SingletonState<bool> ProposerAuthorityRequired { get; set; }
        internal AEDPoSContractContainer.AEDPoSContractReferenceState ConsensusContract {get; set; }
        internal ACS0Container.ACS0ReferenceState GenesisContract { get; set; }
        public MappedState<Hash, ProposalInfo> Proposals { get; set; }
        public MappedState<string, MethodFees> TransactionFees { get; set; }
        
        public SingletonState<ProposerWhiteList> ProposerWhiteList { get; set; }
    }
}