# Switcheo

An implementation of a NEO decentralized exchange

## Manual Withdrawal from V2 Contract

[Read wiki here](https://github.com/ConjurTech/switcheo/wiki/Manual-Withdraws-(Direct-from-V2-Smart-Contract))

# NOTE: The README below is outdated and refers to V1 Contract and will be updated soon to better reflect the V2 contract contents

## Usage

There are 4 main operations for this DApp:

- makeOffer

    This allows users to make a asset swap offer on the contract.

    The params required are:

    1. script hash of the offer maker (invoking user)
    2. script hash of the asset being offered
    3. amount of asset being offered
    4. script hash of the asset wanted in return
    5. amount of asset being wanted

    SystemAssets must be attached if they are part of the fill.

    Once invoked, the offered amount will be transferred to the smart contract, and the offer will be placed on the blockchain for anyone with the corresponding assets to fill.

    NEP-5 tokens and SystemAssets are the currently supported asset types.

- fillOffer

    This allows users to fill an offer on the contract.

    The params required are:

    1. script hash of the offer filler (invoking user)
    2. offer hash of the offer to be filled
    3. the amount offered that should be filled

    SystemAssets must be attached if they are part of the fill.

    Once invoked, the offer corresponding to the second `arg` will be filled. Partial filling is possible. 

    Amounts will be transferred to the maker and filler for withdrawal in a second transaction.

- cancelOffer

    This allows a user to cancel a previous offer that has not been completely filled.

    The params required are:

    1. offer hash to be cancelled

    Once invoked, the offer will be cancelled and any remaining balance will be credited to the user balance for withdrawal in a second transaction.

- withdrawAssets

    This allows users to withdraw their balance in the smart contract.

    The params required (NEP-5) are:

    1. script hash of the user to withdraw balance from
    2. script hash of the asset to withdraw
    3. amount of asset to withdraw

    For SystemAssets, params are not required, but the transaction must be invoked with a TransactionAttribute of Usage `0xd1` and Data `0x01`. We chose an implementation which uses transaction attributes instead of "method arguments" to prevent double withdrawals before main net deployment.

    Our implementation currently does not allow a transfer trading of this balance and this operation must always be called to make use of the swapped asset.

### Offer listing

In order to find offers on the blockchain, list traversal can be used. By querying the contract's storage with a concatenation of the offered and wanted asset script hashes, the head of the list can be obtained.

This will give the offerHash of the latest offer on the trading pair. This allows the offer information to be queried and deserialized. Each offer contains the offerHash of the next offer.

In this way, the list can be traversed entirely, and a order book can be displayed / cached.


## Example

Test on beta client at V2 Testnet at the bottom right https://beta.switcheo.exchange
Or trade on directly on mainnet https://www.switcheo.exchange

## Future work

There remains certain TODOs that are incomplete in V2.

- Usage of DynamicCall

  We hardcoded all known NEP-5 tokens in our contract as `Appcall` can only be done statically at the moment. We hope to push for the implementation of `DynamicCall` in the vm / compiler so that the contract does not need to be updated.
