// SPDX-License-Identifier: MIT
pragma solidity 0.8.36;

import {IERC20Errors} from "@openzeppelin/contracts/interfaces/draft-IERC6093.sol";
import {ERC20Permit} from "@openzeppelin/contracts/token/ERC20/extensions/ERC20Permit.sol";

import {TestUSDC} from "../src/testtokens/TestUSDC.sol";
import {PaymentRouterTestBase} from "./helpers/PaymentRouterTestBase.sol";

contract PaymentRouterPermitTest is PaymentRouterTestBase {
    function test_payWithPermitPaysWithoutPriorApproval() public {
        uint256 amount = 25e6;
        uint256 deadline = block.timestamp + 1 days;
        (uint8 v, bytes32 r, bytes32 s) =
            _signPermit(usdc, PAYER_PRIVATE_KEY, payer, address(router), amount, usdc.nonces(payer), deadline);

        assertEq(usdc.allowance(payer, address(router)), 0);

        vm.expectEmit(true, true, true, true, address(router));
        emit PaymentRecorded(PAYMENT_ID, payer, address(usdc), merchant, amount);

        vm.prank(payer);
        router.payWithPermit(PAYMENT_ID, address(usdc), merchant, amount, deadline, v, r, s);

        assertEq(usdc.balanceOf(merchant), amount);
        assertEq(usdc.nonces(payer), 1);
        assertEq(usdc.allowance(payer, address(router)), 0, "the exact permit allowance is consumed");
        assertEq(usdc.balanceOf(address(router)), 0);
    }

    function test_payWithPermitRevertsForExpiredSignature() public {
        vm.warp(10 days);
        uint256 amount = 1e6;
        uint256 deadline = block.timestamp - 1;
        (uint8 v, bytes32 r, bytes32 s) =
            _signPermit(usdc, PAYER_PRIVATE_KEY, payer, address(router), amount, usdc.nonces(payer), deadline);

        vm.expectRevert(abi.encodeWithSelector(ERC20Permit.ERC2612ExpiredSignature.selector, deadline));
        vm.prank(payer);
        router.payWithPermit(PAYMENT_ID, address(usdc), merchant, amount, deadline, v, r, s);
    }

    function test_payWithPermitRevertsForWrongSigner() public {
        uint256 amount = 1e6;
        uint256 deadline = block.timestamp + 1 days;
        address wrongSigner = vm.addr(OTHER_PRIVATE_KEY);
        (uint8 v, bytes32 r, bytes32 s) =
            _signPermit(usdc, OTHER_PRIVATE_KEY, payer, address(router), amount, usdc.nonces(payer), deadline);

        vm.expectRevert(abi.encodeWithSelector(ERC20Permit.ERC2612InvalidSigner.selector, wrongSigner, payer));
        vm.prank(payer);
        router.payWithPermit(PAYMENT_ID, address(usdc), merchant, amount, deadline, v, r, s);
    }

    function test_payWithPermitRevertsWhenSignatureNamesAnotherSpender() public {
        uint256 amount = 1e6;
        uint256 deadline = block.timestamp + 1 days;
        address anotherSpender = makeAddr("another-spender");
        (uint8 v, bytes32 r, bytes32 s) =
            _signPermit(usdc, PAYER_PRIVATE_KEY, payer, anotherSpender, amount, usdc.nonces(payer), deadline);

        vm.expectPartialRevert(ERC20Permit.ERC2612InvalidSigner.selector);
        vm.prank(payer);
        router.payWithPermit(PAYMENT_ID, address(usdc), merchant, amount, deadline, v, r, s);
    }

    function test_payWithPermitCannotReplayTheSameSignature() public {
        uint256 amount = 1e6;
        uint256 deadline = block.timestamp + 1 days;
        (uint8 v, bytes32 r, bytes32 s) =
            _signPermit(usdc, PAYER_PRIVATE_KEY, payer, address(router), amount, usdc.nonces(payer), deadline);

        vm.prank(payer);
        router.payWithPermit(PAYMENT_ID, address(usdc), merchant, amount, deadline, v, r, s);

        vm.expectPartialRevert(ERC20Permit.ERC2612InvalidSigner.selector);
        vm.prank(payer);
        router.payWithPermit(PAYMENT_ID, address(usdc), merchant, amount, deadline, v, r, s);
    }

    function test_payWithPermitFailsClosedAfterPermitIsFrontRun() public {
        uint256 amount = 1e6;
        uint256 deadline = block.timestamp + 1 days;
        address observer = makeAddr("permit-observer");
        (uint8 v, bytes32 r, bytes32 s) =
            _signPermit(usdc, PAYER_PRIVATE_KEY, payer, address(router), amount, usdc.nonces(payer), deadline);

        // ERC-2612 intentionally allows any submitter. The observer can consume
        // the signed nonce but receives no allowance and cannot act as payer.
        vm.prank(observer);
        usdc.permit(payer, address(router), amount, deadline, v, r, s);

        assertEq(usdc.nonces(payer), 1);
        assertEq(usdc.allowance(payer, address(router)), amount);

        // The Router calls permit again with the now-current nonce, so the old
        // signature fails. No payment event or asset movement can follow.
        vm.expectPartialRevert(ERC20Permit.ERC2612InvalidSigner.selector);
        vm.prank(payer);
        router.payWithPermit(PAYMENT_ID, address(usdc), merchant, amount, deadline, v, r, s);

        assertEq(usdc.balanceOf(merchant), 0);
        assertEq(usdc.balanceOf(address(router)), 0);
        assertEq(usdc.balanceOf(payer), INITIAL_USDC_BALANCE);
        assertEq(usdc.allowance(observer, address(router)), 0);
    }

    function test_payWithPermitDoesNotSupportRelayers() public {
        uint256 amount = 1e6;
        uint256 deadline = block.timestamp + 1 days;
        address relayer = makeAddr("relayer");
        (uint8 v, bytes32 r, bytes32 s) =
            _signPermit(usdc, PAYER_PRIVATE_KEY, payer, address(router), amount, usdc.nonces(payer), deadline);

        vm.expectPartialRevert(ERC20Permit.ERC2612InvalidSigner.selector);
        vm.prank(relayer);
        router.payWithPermit(PAYMENT_ID, address(usdc), merchant, amount, deadline, v, r, s);

        assertEq(usdc.nonces(payer), 0);
        assertEq(usdc.balanceOf(merchant), 0);
    }

    function test_payWithPermitRollsBackNonceAndAllowanceWhenTransferFails() public {
        uint256 amount = INITIAL_USDC_BALANCE + 1;
        uint256 deadline = block.timestamp + 1 days;
        (uint8 v, bytes32 r, bytes32 s) =
            _signPermit(usdc, PAYER_PRIVATE_KEY, payer, address(router), amount, usdc.nonces(payer), deadline);

        vm.expectRevert(
            abi.encodeWithSelector(IERC20Errors.ERC20InsufficientBalance.selector, payer, INITIAL_USDC_BALANCE, amount)
        );
        vm.prank(payer);
        router.payWithPermit(PAYMENT_ID, address(usdc), merchant, amount, deadline, v, r, s);

        assertEq(usdc.nonces(payer), 0, "the permit nonce must roll back with the transaction");
        assertEq(usdc.allowance(payer, address(router)), 0, "the allowance must also roll back");
    }

    function test_payWithPermitRevertsForTokenWithoutPermit() public {
        vm.expectRevert();
        vm.prank(payer);
        router.payWithPermit(PAYMENT_ID, address(token18), merchant, 1e18, block.timestamp + 1 days, 0, 0, 0);
    }

    function test_payWithPermitSignatureCannotCrossTokenContracts() public {
        uint256 amount = 1e6;
        uint256 deadline = block.timestamp + 1 days;
        TestUSDC secondToken = new TestUSDC();
        secondToken.mint(payer, amount);

        (uint8 v, bytes32 r, bytes32 s) =
            _signPermit(usdc, PAYER_PRIVATE_KEY, payer, address(router), amount, usdc.nonces(payer), deadline);

        vm.expectPartialRevert(ERC20Permit.ERC2612InvalidSigner.selector);
        vm.prank(payer);
        router.payWithPermit(PAYMENT_ID, address(secondToken), merchant, amount, deadline, v, r, s);
    }
}
