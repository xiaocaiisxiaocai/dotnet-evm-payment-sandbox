// SPDX-License-Identifier: MIT
pragma solidity 0.8.36;

import {Test} from "forge-std/Test.sol";

import {DeployLocal} from "../script/DeployLocal.s.sol";

contract DeployLocalTest is Test {
    function test_runRevertsOutsideAnvilChain() public {
        DeployLocal deployScript = new DeployLocal();
        vm.chainId(1);

        vm.expectRevert(abi.encodeWithSelector(DeployLocal.WrongChain.selector, 1));
        deployScript.run();
    }
}
