// SPDX-License-Identifier: MIT
pragma solidity 0.8.36;

import {Script} from "forge-std/Script.sol";
import {console2} from "forge-std/console2.sol";

import {PaymentRouter} from "../src/PaymentRouter.sol";
import {TestUSDC} from "../src/testtokens/TestUSDC.sol";
import {TestToken18} from "../src/testtokens/TestToken18.sol";

/// @notice Deploys the Router and test-only tokens to a local Anvil chain.
/// @dev startBroadcast() deliberately reads the sender from Forge's CLI/config.
///      No private key is embedded in source control. Use an unlocked Anvil
///      account when running this local script.
contract DeployLocal is Script {
    uint256 internal constant ANVIL_CHAIN_ID = 31_337;

    error WrongChain(uint256 actualChainId);

    function run() external returns (PaymentRouter router, TestUSDC usdc, TestToken18 token18) {
        // A "local" script must fail closed. Without this guard, a mistaken RPC
        // URL could deploy unrestricted-mint test tokens to a public network.
        if (block.chainid != ANVIL_CHAIN_ID) revert WrongChain(block.chainid);

        vm.startBroadcast();
        router = new PaymentRouter();
        usdc = new TestUSDC();
        token18 = new TestToken18();
        vm.stopBroadcast();

        console2.log("PaymentRouter:", address(router));
        console2.log("TestUSDC:", address(usdc));
        console2.log("TestToken18:", address(token18));
    }
}
