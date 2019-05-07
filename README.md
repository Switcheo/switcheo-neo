# Switcheo (NEO)

Smart contract for a non-custodial decentralized exchange on the NEO blockchain.

This smart contract is live and currently being used on [Switcheo Exchange](https://www.switcheo.exchange)

Unlike a fully on-chain DEX, this contract relies on a 3rd party off-chain order matching system. Order matching and balances can be settled immediately off-chain, allowing instantaneous feedback to the user without waiting for blockchain settlements. This allows a seamless experience for the user trading while maintaining a non-custodial nature of a DEX.

## Safety Features

Both the coodinator and user signature is required for all trade related transactions.
In the event that the exchange has been compromised and can no longer be trusted, users can opt to manually cancel and withdraw their funds via an in built escape hatch without the need of the coordinator's signature.

*Guide to manual cancellations and withdrawals: [Read this wiki](https://github.com/Switcheo/switcheo-neo/wiki/Manual-Withdraws-(Direct-from-V2-Smart-Contract))*

## Initialization

This contract must be initialized with 2 valid coordinator addresses that the contract owner owns:

- Trading Coordinator: Order related functions like making/filling an order must be signed by both the user and the trading coordinator.
- Withdrawal Coordinator: Coordinated withdrawals must be signed by the withdrawal coordinator.

### Instructions

- Change [hard coded](https://github.com/Switcheo/switcheo-neo/blob/a6f3d27dddd38896002b90615ad193d5fb62d568/switcheo/BrokerContract.cs#L104) owner address in the contract to an address you own
  - *Note: ALL transactions that invoke owner functions must be signed by this address*
- [Deploy the contract](https://docs.neo.org/en-us/sc/quickstart/deploy-invoke.html) to a NEO network (Mainnet, Testnet or your own Private net)
- Initialize the contract
  - Invoke `initialize` with the following params:
    1. fee script hash
    2. coordinator script hash
    3. withdraw coordinator script hash
- Whitelist [Old NEP-5 tokens](#differentiation-between-old-vs-new-nep-5-in-this-contract)
  - Invoke `addToWhitelist` with the following params:
    - NEP-5 asset script hash

## Features

### Main operations

- `deposit`

    Make an asset deposit to the contract. Once invoked, the amount will be transferred to the smart contract, and the balance will be on the blockchain to be used in other operations of the smart contract. NEP-5 tokens and SystemAssets are the only supported asset types. SystemAssets must be attached as part of the transaction if it is the asset being deposited

    Params required:

    1. `originator` - Script hash of the user making the deposit.
    2. `assetID` - Script hash of the asset to be deposited.
    3. `amount` - Amount of asset to deposit.

- `makeOffer`

    This allows users to make a offer on the contract by specifying the offer amount and how much of the other asset the user wants in return. Once invoked, the offered amount will reduced from the contract balance of the user and the offer will be placed on the blockchain for anyone with the corresponding assets to fill.

    Params required:

    1. `makerAddress` - Script hash of the user making the offer.
    2. `offerAssetID` - Script hash of the offered asset.
    3. `offerAmount` - Amount to offer.
    4. `wantAssetID` - Script hash of the wanted asset.
    5. `wantAmount` - Amount wanted in return.
    6. `makerFeeAssetID` - Script hash of asset to use as maker fee.
    7. `makerFeeAvailableAmount` - Amount of maker fees reserved to be deducted from this offer when offer is filled.
    8. `nonce` - A nonce to differentiate between offers so that no 2 offers created are the same even if all the parameters are the same.

- `fillOffer`

    Fill an existing offer on the contract. Once invoked, the offer corresponding to the second offerHash will be filled. Partial filling is possible. Both the filler and maker will receive their cut in the trade in their contract balance. A fee can be optionally charged and deducted from the taker/maker within this trade. If the fee asset provided is the same as the offered asset, it will be deducted from the user's cut in the trade else it will be deducted from the user's contract balance.

    Params required:

    1. `fillerAddress` - Script hash of the user making the fill.
    2. `offerHash` - Offer hash of the offer to fill.
    3. `amountToTake` - Amount of the offer's offered asset to take from the offer.
    4. `takerFeeAssetID` - Script hash of the taker fee asset to be charged in this trade.
    5. `takerFeeAmount` - Amount of taker fee to deduct from this fill.
    6. `burnTakerFee` - Choose to burn the taker fee in the smart contract instead of sending to fee address.
    7. `makerFeeAmount` - Amount of maker fee to deduct from the remaining reserved amount for maker fee in the offer.
    8. `burnMakerFee` - Choose to burn the maker fee in the smart contract instead of sending to fee address

- `cancelOffer`

    Cancel a previous offer that has not been completely filled. Once invoked, the offer will be cancelled and any remaining balance will be credited to the user's contract balance.

    Params required:

    1. `offerHash` - Offer hash to be cancelled.

- `withdraw`

    Withdraw user's contract balance in the smart contract to the user's wallet. For withdrawal, params are not required, but the transaction must be invoked with required transaction attributes. System assets require a [2-step withdrawal process](#why-2-step-withdrawals-in-system-assets) while NEP-5 only require a single step.

    Transaction attributes:

    1. `0xa1` - Required - The withdrawal stage either `0x50` (mark system asset withdrawal) or `0x51` (finish withdrawal).
    2. `0xa2` - Required only if NEP-5 - Script hash of the NEP-5 asset to withdraw
    3. `0xa3` - Required only if System Assets - Script hash of the system asset to withdraw
    4. `0xa4` - Required - Script hash to the user address to withdraw.
    5. `0xa5` - Required - Amount of asset to withdraw.
    6. `0x20` - Required only if System Assets or old NEP-5 - Additional witness for verification. Either the withdrawal script hash for system assets or the contract's script hash for [Old NEP-5 tokens](#differentiation-between-old-vs-new-nep-5-in-this-contract). New tokens do not need contract witness.

### Other operations

- `depositFrom` - Same as deposit but uses the transferFrom function of NEP-5 instead of transfer.
- `depositFromNonStandard` - Same as depositFrom but for tokens who implemented transferFrom in a non standard way.
- `onTokenTransfer` - Same as deposit but for use by MCT contract only.
- `burnTokens` - Send tokens to smart contract without increasing any balance effectively burning the tokens.
- `sweepDustTokens` - Sweep multiple small balances into 1 asset's balance.

### Atomic swap operations

- `createAtomicSwap` - Creates an atomic swap offer.
- `executeAtomicSwap` - Execute a verified atomic swap to unlock funds to contract balance.
- `cancelAtomicSwap` - Cancels an atomic swap offer after it has expired.

### Spender contract operations

- `approveSpender` - Approves a 3rd party spender contract to spend this contract's balance via spendFrom
- `rescindApproval` - Unapprove an approved spender contract
- `spendFrom` - Spend a user's contract balance. Only a contract that has been approved by the user is able to use this.

### Manual user operations

- `announceCancel` - Announce for a cancel so that offer can be cancelled without a coordinator signature after a delay time has been met.
- `announceWithdraw` - Announce for a withdraw so that asset can be withdrawn without a withdrawer coordinator signature after a delay time has been met.

### Owner operations

- `freezeTrading` - Freeze makeOffer and fillOffer opetaions.
- `unfreezeTrading` - Unfreeze makeOffer and fillOffer opetaions.
- `setAnnounceDelay` - Set the minimum amount of wait time users need to wait after manually announcing a cancellation or withdraw via the smart contract.
- `setCoordinatorAddress` - Sets the coordinator script hash.
- `setWithdrawCoordinatorAddress` - Sets the withdrawing coordinator script hash.
- `setFeeAddress` - Sets the fee script hash that will receive the fees from making or filling an offer.
- `addToWhitelist` - Adds an old standard NEP5 token to the whitelist so it can be withdrawn.
- `removeFromWhitelist` - Removes an added NEP5 token from the whitelist.
- `sealWhitelist` - Seals the whitelist once all NEP5 tokens will be following the new standard.
- `addSpender` - Adds a spender contract so that users can choose to approve this spender to spend their contract.
- `removeSpender` - Removes a previously added spender contract.

## More information

### Differentiation between Old vs New NEP-5 in this contract

*Old NEP-5 tokens in this contract refers to tokens that does not follow [the standard NEP-5 interface](https://github.com/neo-project/proposals/blob/master/nep-5.mediawiki#transfer). Namely this line: `The function SHOULD check whether the from address equals the caller contract hash. If so, the transfer SHOULD be processed; If not, the function SHOULD use the SYSCALL Neo.Runtime.CheckWitness to verify the transfer.`*

*As of this writing, most tokens on the main network are still following the old standard except for a few tokens like MCT and FTWX. Please do your own due dilligence before adding tokens to the whitelist.*

### Why 2 step withdrawals in system assets

We chose an implementation which uses transaction attributes instead of "method arguments" to prevent double withdrawals that can happen between the verification phase and application phase of NEO.
